using EnvDTE;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using Microsoft.VisualStudio.ComponentModelHost;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.TextManager.Interop;
using System.Linq;
using System.Threading.Tasks;

namespace Gaitway
{
    [Command(PackageIds.WanderCommand)]
    internal sealed class WanderCommand : BaseCommand<WanderCommand>
    {
        private static IWpfTextView GetTextView(IServiceProvider serviceProvider)
        {
            var textManager = (IVsTextManager)serviceProvider.GetService(typeof(SVsTextManager));
            if (textManager != null)
            {
                textManager.GetActiveView(1, null, out var textView);

                var componentModel = (IComponentModel)serviceProvider.GetService(typeof(SComponentModel));
                var adapterService = componentModel?.GetService<Microsoft.VisualStudio.Editor.IVsEditorAdaptersFactoryService>();

                return adapterService?.GetWpfTextView(textView);
            }

            return null;
        }
        private class SymbolName
        {
            public string Name { get; set; }
            public string ContainerName { get; set; }

            public bool Valid => !string.IsNullOrEmpty(ContainerName);
            public string MessageBody => $"{ContainerName}.{Name}";
        }

        private static async Task<SymbolName> GetTargetSymbolAsync(IServiceProvider serviceProvider)
        {
            var textView = GetTextView(serviceProvider);
            var caretPosition = textView.Caret.Position.BufferPosition;

            //For razor files, we have to manually parse the file
            foreach (var buffer in textView.BufferGraph.GetTextBuffers(b => b.ContentType.IsOfType("Razor")))
            {
                var snapshot = buffer.CurrentSnapshot;
                var newCaretPosition = caretPosition.TranslateTo(snapshot, Microsoft.VisualStudio.Text.PointTrackingMode.Negative);
                //Whole file text, as a string
                var text = snapshot.GetText();

                //Analysed syntax tree, note that for body content this tends to be incorrect (it doesn't correctly parse razor files, but it does correctly parse the C# content in them)
                SyntaxTree tree = CSharpSyntaxTree.ParseText(text);

                var root = tree.GetCompilationUnitRoot();

                //What was clicked on
                var node = root.FindToken(newCaretPosition).Parent;
                var parentNode = node.Parent;

                //If this wasn't a member function call
                if (parentNode.GetType() != typeof(MemberAccessExpressionSyntax))
                {
                    //Check if it's a variable declaration (@inject), we can wander to the class
                    if (parentNode.GetType() == typeof(VariableDeclarationSyntax))
                    {
                        //In this case, the token will bind the the @inject, so we skip one word forward to get the type name
                        var line = snapshot.GetText(newCaretPosition.GetContainingLine().Start, newCaretPosition.GetContainingLine().End);
                        var container = line.Split(' ').Skip(1).First();
                        //Return an empty name to signify that we aren't calling a function, but want to wander to the class
                        return new SymbolName { Name = "", ContainerName = container };
                    }
                    //Otherwise don't wander
                    return new SymbolName { Name = "", ContainerName = "" };
                }

                //Find the object this node is called on (assuming it's a function call)
                var caller = node.Ancestors().OfType<MemberAccessExpressionSyntax>().FirstOrDefault();
                if (caller == null)
                {
                    return new SymbolName { Name = "", ContainerName = "" };
                }

                //The node will contain the text "caller.function", so extract the caller name
                var source = new string(caller.ToString().TakeWhile(c => c != '.').ToArray());

                //Find all lines containing the source
                var lines = text.Split('\n').Where(l => l.Contains(source));

                //Filter to those with inject statements
                var injects = lines.Where(l => l.Contains("@inject")).ToList();

                if (injects.Count != 1)
                {
                    return new SymbolName { Name = "", ContainerName = "" };
                }

                //Extract the class from the statement (first whole word after @inject)
                var className = injects.First().Trim().Split(' ').Skip(1).First();

                return new SymbolName { Name = node.ToString(), ContainerName = className };
            }

            //Find the regular c# buffer
            foreach (var buffer in textView.BufferGraph.GetTextBuffers(b => b.ContentType.IsOfType("CSharp")))
            {
                caretPosition = caretPosition.TranslateTo(buffer.CurrentSnapshot, Microsoft.VisualStudio.Text.PointTrackingMode.Negative);

                var document = caretPosition.Snapshot.GetRelatedDocumentsWithChanges().FirstOrDefault();

                if (document != null)
                {
                    //Get the syntax tree and semantic model for the document, and extract the symbol at the caret position
                    var syntaxNode = await document.GetSyntaxRootAsync().ConfigureAwait(true);
                    var semanticModel = await document.GetSemanticModelAsync().ConfigureAwait(true);
                    var symbol = (syntaxNode.FindToken(caretPosition).Parent, semanticModel);
                    return new SymbolName()
                    {
                        Name = symbol.Item1.ToString(),
                        ContainerName = symbol.Item2.GetSymbolInfo(symbol.Item1).Symbol?.ContainingType?.ToString()
                    };
                }
            }

            return new SymbolName { Name = "", ContainerName = "" };
        }

        protected override async Task ExecuteAsync(OleMenuCmdEventArgs e)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            await VS.StatusBar.StartAnimationAsync(StatusAnimation.Sync);
            await VS.StatusBar.ShowMessageAsync("Sending Wander...");

            if (PipeLink.Instance == null)
            {
                await VS.StatusBar.ShowMessageAsync("Unable To Wander - Listener isn't running!");
                return;
            }
            if (!PipeLink.Instance.Connected)
            {
                await VS.StatusBar.ShowMessageAsync("Unable To Wander - No Clients To Wander To!");
                return;
            }

            var symbol = await GetTargetSymbolAsync(ServiceProvider.GlobalProvider);

            if (!symbol.Valid)
            {
                await VS.StatusBar.ShowMessageAsync("Unable To Wander - No Symbol Found!");
                return;
            }

            await PipeLink.Instance?.SendMessageAsync(symbol.MessageBody);

            await VS.StatusBar.EndAnimationAsync(StatusAnimation.Sync);

            await VS.StatusBar.ShowMessageAsync("Request Sent!");
        }

        protected override void BeforeQueryStatus(EventArgs e)
        {
            base.BeforeQueryStatus(e);

            var potentialSymbol = ThreadHelper.JoinableTaskFactory.Run(async () =>
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                return await GetTargetSymbolAsync(ServiceProvider.GlobalProvider);
            });

            Command.Visible = Command.Enabled = potentialSymbol.Valid;
            Command.Text = string.IsNullOrEmpty(potentialSymbol.Name) ? "Wander To Class" : "Wander To Method";
        }
    }
}