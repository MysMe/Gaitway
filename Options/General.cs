using System.Collections.Generic;
using System.Collections.Specialized;
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
        [Category("Search Namespaces")]
        [DisplayName("Search Namespaces")]
        [Description("Which namespaces will be searched when wandering, these namespaces should contain controllers.")]
        [TypeConverter(typeof(StringArrayConverter))]
        public string[] SearchNamespaces { get; set; }
    }
}
