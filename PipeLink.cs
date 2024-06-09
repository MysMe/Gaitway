using Microsoft.VisualStudio.Threading;
using System.Collections.Concurrent;
using System.IO;
using System.IO.Pipes;
using System.Threading;

namespace Gaitway
{

    //Basic pipe connection, write-only, reads are managed through callbacks
    internal interface IPipeConnection
    {
        public Task SendMessageAsync(string message);
    }

    internal static class PipeConnectionHelpers
    {
        public const string PipeName = "gaitwaypipe";
        public const string ControlPipeName = "gaitwaycontrolpipe";
    }

    //Wrapped pipe stream to allow for easy sending and receiving of messages
    internal class PipeConnection<T> : IPipeConnection, IDisposable where T : PipeStream
    {
        private T Connection;
        private Func<string, PipeConnection<T>, Task> OnReceive;
        private Func<PipeConnection<T>, Task> OnDisconnect;

        private StreamReader ReadStream;
        private StreamWriter WriteStream;
        private CancellationTokenSource TokenSource = new();

        public PipeConnection(T stream, Func<string, PipeConnection<T>, Task> onReceive, Func<PipeConnection<T>, Task> onDisconnect)
        {
            Connection = stream;
            OnReceive = onReceive;
            OnDisconnect = onDisconnect;
            ReadStream = new StreamReader(Connection, System.Text.Encoding.UTF8, true, 1024, leaveOpen: true);
            WriteStream = new StreamWriter(Connection, System.Text.Encoding.UTF8, 1024, leaveOpen: true);
            //Start polling for messages
            _ = Task.Run(() => PollAsync());
        }

        private async Task PollAsync()
        {
            var token = TokenSource.Token;
            while (!token.IsCancellationRequested)
            {
                //Wait for either readline or cancellation
                var messageTask = ReadStream.ReadLineAsync();
                var cancellationTask = Task.Delay(-1, token);
                var completedTask = await Task.WhenAny(messageTask, cancellationTask);
                if (completedTask == cancellationTask)
                {
                    //If the cancellation task completes, stop polling, the link is being disposed
                    return;
                }
                var message = await messageTask;

                if (!Connection.IsConnected)
                {
                    //If the connection is closed, call the disconnect handler and stop polling
                    await OnDisconnect(this);
                    return;
                }
                if (message != null)
                {
                    //If we get here, the connection is still open, so we can call the receive handler
                    await OnReceive(message, this);
                }
            }
        }
        public async Task SendMessageAsync(string message)
        {
            await WriteStream.WriteLineAsync(message);
            await WriteStream.FlushAsync();
        }

        public bool IsConnected => Connection.IsConnected;

        public void Dispose()
        {
            TokenSource.Cancel();
            Connection.Dispose();
            Connection = null!;
        }
    }

    internal class PipeServer : IPipeConnection, IDisposable
    {
        //Listens for new connections, but never sends data
        private NamedPipeServerStream listener;
        private ConcurrentDictionary<PipeConnection<NamedPipeServerStream>, byte> clients = new();
        private Func<string, Task> OnReceive;

        private CancellationTokenSource WaitTokenSource = new();

        //Use a pipe with a limit of 1 connection to ensure we only ever have a single server instance
        //We can let the OS manage who actually becomes the server, as it's irrelevant for our purposes
        private NamedPipeServerStream control;

        public PipeServer(Func<string, Task> onReceive)
        {
            //Creating the control pipe will throw an exception if the pipe already exists (in another process)
            control = new NamedPipeServerStream(PipeConnectionHelpers.ControlPipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
            listener = GetListener();
            OnReceive = onReceive;
        }

        public async Task StartListeningAsync()
        {
            await WaitForConnectionAsync();
        }

        private static NamedPipeServerStream GetListener() => new NamedPipeServerStream(PipeConnectionHelpers.PipeName, PipeDirection.InOut, -1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);

        private async Task WaitForConnectionAsync()
        {
            var token = WaitTokenSource.Token;
            while (!token.IsCancellationRequested)
            {
                await listener.WaitForConnectionAsync(token);
                //When a connection is made, create a new pipe connection and add it to the list
                var client = new PipeConnection<NamedPipeServerStream>(listener, async (message, source) =>
                    {
                        await ForwardReceivedMessageAsync(message, source);
                        await OnReceive(message);
                    },
                    //When the connection is closed, remove it from the list
                    (source) => { clients.TryRemove(source, out _); return Task.CompletedTask; });
                clients.TryAdd(client, 0);
                listener = GetListener();
            }
        }

        public async Task SendMessageAsync(string message)
        {
            //Send the message to all clients, we don't allow individual messaging
            foreach (var client in clients.Keys)
            {
                await client.SendMessageAsync(message);
            }
        }
        private async Task ForwardReceivedMessageAsync(string message, PipeConnection<NamedPipeServerStream> source)
        {
            //When we receive a message, forward it to all clients except the source
            foreach (var client in clients.Keys)
            {
                if (client != source)
                    await client.SendMessageAsync(message);
            }
        }
        public bool Connected => clients.Count != 0;
        public void Dispose()
        {
            WaitTokenSource.Cancel();
            listener.Dispose();
            control.Dispose();
            foreach (var client in clients.Keys)
            {
                client.Dispose();
            }
            clients.Clear();
            listener = null!;
            control = null!;
            clients.Clear();
        }
    }

    internal class PipeClient : IDisposable
    {
        private PipeConnection<NamedPipeClientStream> connection;

        public PipeClient(Func<string, Task> onReceive, Func<PipeClient, Task> onDisconnect)
        {
            var stream = new NamedPipeClientStream(".", PipeConnectionHelpers.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            stream.Connect();
            connection = new PipeConnection<NamedPipeClientStream>(stream, (message, source) => onReceive(message), (_) => onDisconnect(this));
        }

        public async Task SendMessageAsync(string message)
        {
            await connection.SendMessageAsync(message);
        }
        public bool Connected => connection.IsConnected;
        public void Dispose()
        {
            connection.Dispose();
            connection = null!;
        }
    }

    //Acts as either a server or a client
    public class PipeLink : IDisposable
    {
        private PipeServer server;
        private PipeClient client;
        public PipeLink(Func<string, Task> onReceive)
        {
            TryHost(onReceive);
        }

        private void TryHost(Func<string, Task> onReceive)
        {
            server = null;
            client = null;
            try
            {
                server = new PipeServer(onReceive);
            }
            catch (Exception)
            {
                client = new PipeClient(onReceive, (_) => { TryHost(onReceive); return Task.CompletedTask; });
            }
        }

        public async Task SendMessageAsync(string message)
        {
            await (server?.SendMessageAsync(message) ?? client!.SendMessageAsync(message));
        }

        public async Task StartListeningAsync()
        {
            await (server?.StartListeningAsync() ?? Task.CompletedTask);
        }

        public bool Connected => server?.Connected ?? client!.Connected;

        public void Dispose()
        {
            server?.Dispose();
            client?.Dispose();
            server = null;
            client = null;
        }

        public static PipeLink Instance { get; set; }
    }
}
