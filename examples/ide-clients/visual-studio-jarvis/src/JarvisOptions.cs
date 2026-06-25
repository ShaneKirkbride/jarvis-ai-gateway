using Microsoft.VisualStudio.Shell;
using System.ComponentModel;

namespace Jarvis.VisualStudio.Example;

public sealed class JarvisOptions : DialogPage
{
    [Category("Jarvis Gateway")]
    [DisplayName("Base URL")]
    [Description("Jarvis gateway /v1 base URL.")]
    public string BaseUrl { get; set; } = "https://<gateway>/v1";

    [Category("Jarvis Gateway")]
    [DisplayName("Model")]
    [Description("Gateway model alias to use for chat requests.")]
    public string Model { get; set; } = "jarvis2-chat";

    [Category("Jarvis Gateway")]
    [DisplayName("Confirm Before Send")]
    [Description("Prompt before sending selected text to Jarvis.")]
    public bool ConfirmBeforeSend { get; set; } = true;
}
