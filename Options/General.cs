using Microsoft.VisualStudio.Shell.Interop;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Web.UI.WebControls;

namespace Gaitway
{
    internal partial class OptionsProvider
    {
        [ComVisible(true)]
        public class GeneralOptions : BaseOptionPage<General> { }
    }

    public class General : BaseOptionModel<General>
    {
        private static bool Registered = false;
        public General()
        {
            if (!Registered)
                Saved += OnSettingsSaved;
            Registered = true;
        }

        [Category("Search Namespaces")]
        [DisplayName("Search Namespaces")]
        [Description("Which namespaces will be searched when wandering, these namespaces should contain controllers.")]
        [TypeConverter(typeof(StringArrayConverter))]
        public string[] SearchNamespaces { get; set; }

        private void OnSettingsSaved(General e)
        {
            if (PipeLink.Instance?.Connected ?? false)
            {
                ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
                {
                    await VS.MessageBox.ShowAsync("Other instances of Visual Studio are currently connected.",
                    "You will need to restart those instances for the namespace change to take effect.", OLEMSGICON.OLEMSGICON_WARNING, OLEMSGBUTTON.OLEMSGBUTTON_OK);
                }).FireAndForget();
            }
        }
    }
}
