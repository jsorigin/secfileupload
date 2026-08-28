using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc.RazorPages;
using SecureUpload.Web.Security;

namespace SecureUpload.Web.Pages;

public sealed partial class UploadModel(
    AllowedOriginPolicy origins,
    IConfiguration configuration) : PageModel
{
    public string Title { get; private set; } = "Secure file upload";
    public string HelpText { get; private set; } = "Files are checked for malware before they become available.";
    public string Theme { get; private set; } = "light";
    public string AccentColor { get; private set; } = "#2563eb";
    public string ClientConfiguration { get; private set; } = "{}";

    public void OnGet(string? parentOrigin)
    {
        Title = configuration["Presentation:Title"] ?? Title;
        HelpText = configuration["Presentation:HelpText"] ?? HelpText;
        Theme = configuration["Presentation:Theme"] is "dark" ? "dark" : "light";
        var configuredAccent = configuration["Presentation:AccentColor"];
        if (configuredAccent is not null && HexColor().IsMatch(configuredAccent))
        {
            AccentColor = configuredAccent;
        }

        ClientConfiguration = JsonSerializer.Serialize(new
        {
            targetOrigin = origins.GetMessageTarget(parentOrigin)
        }).Replace("<", "\\u003c", StringComparison.Ordinal);
    }

    [GeneratedRegex("^#[0-9a-fA-F]{6}$")]
    private static partial Regex HexColor();
}
