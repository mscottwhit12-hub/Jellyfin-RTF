namespace Jellyfin.Plugin.RaceToFinish.Services;

/// <summary>Payload File Transformation hands to <see cref="IndexHtmlTransformer.Transform"/>.</summary>
public class IndexHtmlPayload
{
    /// <summary>Gets or sets the current contents of index.html.</summary>
    public string? Contents { get; set; }
}

/// <summary>
/// Adds the plugin's stylesheet and script to jellyfin-web's index.html as it is served.
/// Nothing on disk is changed. Called by File Transformation through reflection: keep the
/// class name, method name and signature stable.
/// </summary>
public static class IndexHtmlTransformer
{
    private const string Marker = "data-rtf-plugin";

    /// <summary>Returns index.html with our tags added (once).</summary>
    /// <param name="payload">The page as File Transformation currently has it.</param>
    /// <returns>The transformed page.</returns>
    public static string Transform(IndexHtmlPayload payload)
    {
        string html = payload.Contents ?? string.Empty;
        if (html.Contains(Marker, StringComparison.Ordinal))
        {
            return html;
        }

        string version = typeof(IndexHtmlTransformer).Assembly.GetName().Version?.ToString() ?? "0";

        // Relative to /web/index.html, so it also works when the server uses a base URL.
        string css = $"<link rel=\"stylesheet\" {Marker} href=\"../RaceToFinish/web/rtf.css?v={version}\" />";
        string js = $"<script {Marker} src=\"../RaceToFinish/web/rtf.js?v={version}\" defer></script>";

        return html
            .Replace("</head>", css + "</head>", StringComparison.Ordinal)
            .Replace("</body>", js + "</body>", StringComparison.Ordinal);
    }
}
