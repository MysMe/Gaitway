﻿using EnvDTE;
using Microsoft.VisualStudio.Shell.Interop;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using VSLangProj;

namespace Gaitway
{
    [PackageRegistration(UseManagedResourcesOnly = true, AllowsBackgroundLoading = true)]
    [ProvideService(typeof(Listener), IsAsyncQueryable = true)]
    [ProvideAutoLoad(UIContextGuids80.NoSolution, PackageAutoLoadFlags.BackgroundLoad)]
    [Guid(Listener.PackageGuidString)]
    [SuppressMessage("StyleCop.CSharp.DocumentationRules", "SA1650:ElementDocumentationMustBeSpelledCorrectly", Justification = "pkgdef, VS and vsixmanifest are valid VS terms")]
    public sealed class Listener : AsyncPackage
    {
        public const string PackageGuidString = "372bd7f4-a33d-40ed-acd7-d72baceccf60";

        public Listener()
        {
        }

        #region Package Members

        //Invoked when we receive a wander command
        private async Task OnReceiveAsync(string content)
        {
            try
            {
                await VS.StatusBar.StartAnimationAsync(StatusAnimation.Sync);
                await VS.StatusBar.ShowMessageAsync("Received Wander: " + content);
                var words = content.Split('.');
                if (words.Length >= 2)
                {
                    var functionName = words[words.Length - 1];
                    var className = words[words.Length - 2];

                    if (!await FindAndNavigateToMethodAsync(className, functionName))
                    {
                        await VS.StatusBar.ShowMessageAsync("Wander failed to find method: " + content);
                    }
                }
                else
                {
                    await VS.StatusBar.ShowMessageAsync("Wander failed: Mangled wander.");
                }
                await VS.StatusBar.EndAnimationAsync(StatusAnimation.Sync);
            }
            catch (Exception e)
            {
                await VS.StatusBar.ShowMessageAsync("Wander failed: " + e.Message);
            }
        }

        private async Task WanderToAsync(DTE dte, string fileName, TextPoint point)
        {
            await JoinableTaskFactory.SwitchToMainThreadAsync();
            //Open the file
            dte.ItemOperations.OpenFile(fileName);
            //Spin until the file is open
            while (!dte.ItemOperations.IsFileOpen(fileName)) { }
            //Delay so the ide can catch up
            await Task.Delay(100);
            var window = dte.MainWindow;
            if (window != null)
            {
                window.Activate();
                window.SetFocus();

                //Adjusting the window state forces it to the foreground, so we swap it and then revert it
                var originalState = window.WindowState;
                //Assume it was maximised if we're bringing it up from minimised, we can't know the actual state from here
                if (originalState == vsWindowState.vsWindowStateMaximize)
                {
                    window.WindowState = vsWindowState.vsWindowStateMaximize;
                }
                window.WindowState = originalState == vsWindowState.vsWindowStateNormal ? vsWindowState.vsWindowStateMaximize : vsWindowState.vsWindowStateNormal;
                window.WindowState = originalState;
            }
            dte.ExecuteCommand("Edit.GoTo", point.Line.ToString());
        }

        private async Task<bool> FindAndNavigateToMethodAsync(string className, string methodName)
        {
            //The class name will be formatted as "IClassClient", so we must reformat it to "ClassController"
            className = className.Substring(1);
            className = className.Remove(className.Length - 6);
            className = className + "Controller";

            if (!string.IsNullOrWhiteSpace(methodName))
            {
                //The function name (if provided) will have been postfixed with "Async", so we must remove it
                methodName = methodName.Substring(0, methodName.Length - 5);
            }

            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            DTE dte = ServiceProvider.GlobalProvider.GetService(typeof(DTE)) as DTE;
            if (dte == null)
            {
                return false;
            }

            //Get the active solution
            var solution = dte.Solution;
            if (solution == null)
            {
                return false;
            }

            //Iterate through all projects in the solution
            foreach (EnvDTE.Project project in solution.Projects)
            {
                //Check if the project is a C# project
                if (project.Kind == PrjKind.prjKindCSharpProject)
                {
                    //Get the code model for the project
                    CodeModel codeModel = project.CodeModel;
                    if (codeModel != null)
                    {
                        CodeClass codeClass = null;
                        //Iterate through all the namespaces in the search namespaces setting to find the class
                        foreach (var namespaceName in General.Instance.SearchNamespaces)
                        {
                            codeClass = codeModel.CodeTypeFromFullName(namespaceName + "." + className) as CodeClass;
                            if (codeClass != null)
                                break;
                        }
                        if (codeClass != null)
                        {
                            //If we don't have a method name, just wander to the class declaration
                            if (string.IsNullOrWhiteSpace(methodName))
                            {
                                await WanderToAsync(dte, codeClass.ProjectItem.FileNames[0], codeClass.GetStartPoint(vsCMPart.vsCMPartHeader));
                                return true;
                            }
                            //Otherwise, find the method in the class and wander to it
                            foreach (CodeElement member in codeClass.Members)
                            {
                                if (member.Kind == vsCMElement.vsCMElementFunction && member.Name == methodName)
                                {
                                    await WanderToAsync(dte, member.ProjectItem.FileNames[0], (member as CodeFunction).GetStartPoint(vsCMPart.vsCMPartHeader));
                                    return true;
                                }
                            }
                        }
                    }
                }
            }
            return false;
        }

        //Invoked at package load time
        protected override async Task InitializeAsync(CancellationToken cancellationToken, IProgress<ServiceProgressData> progress)
        {
            await base.InitializeAsync(cancellationToken, progress);
            //Encourage the user to set their namespaces
            if (General.Instance.SearchNamespaces == null || General.Instance.SearchNamespaces.Count() == 0)
            {
                //Display warning modal
                await VS.MessageBox.ShowAsync("No controller namespaces have been configured for Gaitway and it will not be able to find any controllers.",
                    "Please set your namespaces from the options page. You will need to restart all VS instances for the change to take effect.", OLEMSGICON.OLEMSGICON_WARNING, OLEMSGBUTTON.OLEMSGBUTTON_OK);
            }
            //Start the pipe listener
            if (PipeLink.Instance == null)
            {
                PipeLink.Instance = new PipeLink(OnReceiveAsync);
                _ = Task.Run(() => PipeLink.Instance.StartListeningAsync());
            }
        }

        #endregion
    }
}