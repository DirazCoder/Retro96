using System;

namespace Retro96.Engine;

public static class ErrorPage
{
    private static string EscapeHtml(string value)
    {
        if (string.IsNullOrEmpty(value))
            return string.Empty;
        return value.Replace("&", "&amp;")
                    .Replace("<", "&lt;")
                    .Replace(">", "&gt;")
                    .Replace("\"", "&quot;")
                    .Replace("'", "&#39;");
    }

    private static string Footer => @"
<div class=""footer"">
  <span>&copy; DirazCoder 2026</span>
  <a href=""retro96://home"">Home</a>
</div>";

    private static string MakePageShell(string titleColor, string titleText, string bodyContent) => $@"<!DOCTYPE HTML PUBLIC ""-//W3C//DTD HTML 4.01//EN"" ""http://www.w3.org/TR/html4/strict.dtd"">
<html>
<head>
  <title>{EscapeHtml(titleText)}</title>
  <style type=""text/css"">
    body {{ margin: 0; padding: 24px; background-color: #e9edf2; color: #202833; font-family: Arial, Helvetica, sans-serif; font-size: 10pt; }}
    a {{ color: #003399; }}
    a:visited {{ color: #663399; }}
    .page {{ width: 680px; margin: 0 auto; border: 1px solid #9aaabd; background-color: #ffffff; }}
    .titlebar {{ padding: 9px 12px; background-color: {titleColor}; color: #ffffff; }}
    .title {{ margin: 0; font-size: 14pt; font-weight: bold; }}
    .brand {{ float: right; color: #dbe6f2; font-size: 9pt; font-weight: bold; }}
    .content {{ padding: 18px; }}
    .warning {{ margin-bottom: 16px; border: 1px solid #c5cfdb; background-color: #f2f5f9; }}
    .warning-mark {{ float: left; width: 5px; margin-right: 10px; background-color: {titleColor}; }}
    .warning-copy {{ padding: 8px; }}
    .warning-title {{ color: #202833; font-weight: bold; }}
    .warning-subtitle {{ color: #526579; font-size: 9pt; }}
    .muted {{ color: #687887; font-size: 9pt; }}
    .lead {{ font-size: 11pt; }}
    .code {{ font-family: ""Courier New"", monospace; }}
    hr {{ height: 1px; border: 0; background-color: #c5cfdb; }}
    .details {{ width: 100%; border-collapse: collapse; background-color: #f2f5f9; }}
    .details td {{ padding: 6px; border: 1px solid #c5cfdb; text-align: left; vertical-align: top; }}
    .footer {{ margin-top: 20px; padding-top: 8px; border-top: 1px solid #d5dce4; color: #687887; font-size: 9pt; }}
    .footer a {{ float: right; }}
    .actions {{ margin-top: 14px; }}
  </style>
</head>
<body>
  <div class=""page"">
    <div class=""titlebar"">
      <span class=""brand"">Retro96</span>
      <h1 class=""title"">{EscapeHtml(titleText)}</h1>
    </div>
    <div class=""content"">
{bodyContent}
{Footer}
    </div>
  </div>
</body>
</html>";

    private static string WarningBanner(string title, string subtitle, string border, string fg) => $@"
<div class=""warning"">
  <div class=""warning-mark"" style=""background-color: {border}"">&nbsp;</div>
  <div class=""warning-copy"">
    <span class=""warning-title"" style=""color: {fg}"">{EscapeHtml(title)}</span><br>
    <span class=""warning-subtitle"">{EscapeHtml(subtitle)}</span>
  </div>
</div>";

    /// <summary>
    /// Generate HTML for network errors (connection failed, DNS failure, etc.)
    /// </summary>
    public static string NetworkError(string url, string message)
    {
        string escapedUrl = EscapeHtml(url);
        string escapedMessage = EscapeHtml(message);
        string body = $@"
{WarningBanner("Unable to Connect", "A network connection could not be established.", "#cc0000", "#800000")}
<p><b>Requested address:</b></p>
<blockquote><code class=""code""><b>{escapedUrl}</b></code></blockquote>
<p><b>The technical reason:</b> {escapedMessage}</p>
<hr>
<p><b>Here are a few things you can try to fix this:</b></p>
<ul>
  <li>Double-check the address for any typos.</li>
  <li>Make sure your internet connection is actually plugged in and working.</li>
  <li>Hit <b>Reload</b> &mdash; sometimes the connection just drops for a moment.</li>
  <li>The website might be temporarily down for maintenance, so try again later.</li>
</ul>";
        return MakePageShell("#000080", "Network Error", body);
    }

    /// <summary>
    /// Generate HTML for DNS resolution failures.
    /// </summary>
    public static string DnsFailure(string host)
    {
        string escapedHost = EscapeHtml(host);
        string body = $@"
{WarningBanner("Server Not Found", "The host name could not be resolved by DNS.", "#cc9900", "#996600")}
<p>The host name <b><code class=""code"">{escapedHost}</code></b> could not be found on the network.</p>
<p><b>This usually means one of a few things:</b></p>
<ul>
  <li>You might have accidentally mistyped the address.</li>
  <li>The website might not exist anymore, or the domain expired.</li>
  <li>Your computer's DNS settings might be acting up.</li>
  <li>Your internet connection might have dropped.</li>
</ul>
<hr>
<p class=""muted"">DNS Error &mdash; The hostname could not be resolved.</p>";
        return MakePageShell("#800000", "DNS Failure", body);
    }

    /// <summary>
    /// Generate HTML for certificate errors.
    /// The "Accept Risk" button uses the shell's retro96:// scheme hook.
    /// </summary>
    public static string CertificateError(string url, string message)
    {
        string escapedUrl = EscapeHtml(url);
        string escapedMessage = EscapeHtml(message);
        string body = $@"
{WarningBanner("Security Warning", "This site's certificate could not be verified.", "#cc9900", "#996600")}
<p><b><span class=""lead"">Heads up!</span></b> This site's security certificate couldn't be verified.
Proceeding could expose your personal info to prying eyes.</p>
<hr>
<p><b>URL:</b> <code class=""code"">{escapedUrl}</code></p>
<p><b>What went wrong:</b> {escapedMessage}</p>
<p>A valid certificate is how a website proves it is who it claims to be.
Without one, your connection isn't secure, and someone else might be able to
read the information you send.</p>
<hr>
<p><b class=""lead"">Are you sure you want to continue anyway?</b></p>
<form onsubmit=""if(confirm('WARNING: You are about to accept an untrusted certificate.\n\nProceed only if you understand the risks.\n\nContinue?')) {{ window.acceptCertRisk && window.acceptCertRisk(); }} return false;"">
  <div class=""actions"">
    <input type=""submit"" value=""Accept Risk and Continue"">
    <input type=""button"" value=""Go Back (Recommended)"" onclick=""history.back()"">
  </div>
</form>
<p class=""muted"">I've blocked this page to keep you safe.</p>";
        return MakePageShell("#800000", "Security Warning \u2014 Certificate Error", body);
    }

    /// <summary>
    /// Generate HTML for connection timeout errors.
    /// </summary>
    public static string Timeout(string url)
    {
        string escapedUrl = EscapeHtml(url);
        string body = $@"
{WarningBanner("Connection Timed Out", "The server did not respond in time.", "#cc0000", "#800000")}
<p>The server at <b><code class=""code"">{escapedUrl}</code></b> did not respond before the connection timed out.</p>
<p>I waited as long as I could, but eventually had to give up.</p>
<hr>
<p><b>Why did this happen?</b></p>
<ul>
  <li>The server might be really busy right now &mdash; try again in a minute.</li>
  <li>Your internet connection might be running a bit slow.</li>
  <li>A firewall might be blocking the connection.</li>
  <li>The website might have crashed or gone offline.</li>
</ul>
<p><a href=""retro96://reload""><b>Reload this page</b></a></p>";
        return MakePageShell("#000080", "Connection Timed Out", body);
    }

    /// <summary>
    /// Generate HTML for too many redirects.
    /// </summary>
    public static string TooManyRedirects(string url)
    {
        string escapedUrl = EscapeHtml(url);
        string body = $@"
{WarningBanner("Redirect Loop Detected", "The page redirected too many times.", "#cc0000", "#800000")}
<p>To prevent an endless loop, Retro96 stopped loading this page after repeated redirects.</p>
<blockquote><code class=""code""><b>{escapedUrl}</b></code></blockquote>
<p>The page redirected more than <b>5 times</b>. This is almost always a mistake
on the website's end.</p>
<hr>
<p><b>There's not much you can do here.</b> You might want to let the website
administrator know so they can fix their server configuration.</p>";
        return MakePageShell("#000080", "Too Many Redirects", body);
    }

    /// <summary>
    /// Generate HTML for 404 Not Found errors.
    /// </summary>
    public static string NotFound(string url)
    {
        string escapedUrl = EscapeHtml(url);
        string body = $@"
{WarningBanner("404 \u2014 Page Not Found", "The requested URL could not be found on the server.", "#cc0000", "#800000")}
<p>The page or file you're looking for doesn't seem to exist:</p>
<blockquote><code class=""code""><b>{escapedUrl}</b></code></blockquote>
<hr>
<p><b>Here are some ideas:</b></p>
<ul>
  <li>Double-check the address for typos &mdash; web addresses are case-sensitive.</li>
  <li>The page might have been moved or deleted recently.</li>
  <li>Try going to the website's home page and navigating from there.</li>
</ul>
<p class=""muted"">HTTP 404 Not Found</p>";
        return MakePageShell("#000080", "404 \u2014 Page Not Found", body);
    }

    /// <summary>
    /// Generate HTML for 403 Access Denied errors.
    /// </summary>
    public static string AccessDenied(string url)
    {
        string escapedUrl = EscapeHtml(url);
        string body = $@"
{WarningBanner("403 \u2014 Access Denied", "You do not have permission to view this page.", "#cc0000", "#800000")}
<p>Sorry, you don't have permission to view this page.</p>
<p><b>URL:</b> <code class=""code"">{escapedUrl}</code></p>
<p>The server understood the request, but it's refusing to show you anything. This could be because:</p>
<ul>
  <li>You need to log in to see this page.</li>
  <li>Your account doesn't have the right privileges.</li>
  <li>The website is blocking your IP address.</li>
  <li>The owner has locked this page down completely.</li>
</ul>
<hr>
<p class=""muted"">HTTP 403 Forbidden</p>";
        return MakePageShell("#800000", "403 \u2014 Access Denied", body);
    }

    /// <summary>
    /// Generate HTML for 500 Internal Server Error.
    /// </summary>
    public static string ServerError(string url, string? detail = null)
    {
        string escapedUrl = EscapeHtml(url);
        string detailHtml = string.IsNullOrEmpty(detail)
            ? ""
            : $"<p><b>Message from server:</b> {EscapeHtml(detail!)}</p>";
        string body = $@"
{WarningBanner("500 \u2014 Internal Server Error", "Something went wrong on the server.", "#cc0000", "#800000")}
<p>The server encountered an error and could not finish loading this page.</p>
<blockquote><code class=""code""><b>{escapedUrl}</b></code></blockquote>
{detailHtml}
<hr>
<p><b>This isn't a problem with Retro96.</b> The site's code likely crashed.</p>
<ul>
  <li>Try refreshing in a few minutes &mdash; they might be restarting things.</li>
  <li>If it keeps happening, you might want to contact the site's admin.</li>
</ul>
<p class=""muted"">HTTP 500 Internal Server Error</p>";
        return MakePageShell("#800000", "500 \u2014 Internal Server Error", body);
    }

    /// <summary>
    /// Generate HTML for malformed or invalid URLs.
    /// </summary>
    public static string MalformedUrl(string url)
    {
        string escapedUrl = EscapeHtml(url);
        string body = $@"
{WarningBanner("Invalid Address", "The address you entered could not be parsed.", "#cc0000", "#800000")}
<p>I can't open this address because it doesn't look quite right:</p>
<blockquote><code class=""code""><b>{escapedUrl}</b></code></blockquote>
<p><b>Common mistakes to look out for:</b></p>
<ul>
  <li>Forgetting the <code class=""code"">http://</code> at the very beginning.</li>
  <li>Using spaces instead of <code class=""code"">%20</code>.</li>
  <li>Accidentally including strange characters.</li>
  <li>Mixing up the slashes after the domain name.</li>
</ul>
<hr>
<p class=""muted"">A valid address looks like: <code class=""code"">http://www.example.com/page.html</code></p>";
        return MakePageShell("#000080", "Invalid Address", body);
    }

    /// <summary>
    /// Generate HTML for proxy errors.
    /// </summary>
    public static string ProxyError(string proxyHost, string message)
    {
        string escapedProxy = EscapeHtml(proxyHost);
        string escapedMessage = EscapeHtml(message);
        string body = $@"
{WarningBanner("Proxy Server Error", "The proxy server could not be reached or returned an error.", "#cc0000", "#800000")}
<p>You're set up to use a proxy server, but I couldn't reach it or it sent back an error.</p>
<table class=""details"">
<tr><td><b>Proxy server:</b></td><td><code class=""code"">{escapedProxy}</code></td></tr>
<tr><td><b>Error:</b></td><td>{escapedMessage}</td></tr>
</table>
<br>
<p><b>What you can do:</b></p>
<ul>
  <li>Check your proxy settings under <b>Options &rarr; Network Preferences</b>.</li>
  <li>Get in touch with your network admin or ISP.</li>
  <li>If you don't actually need a proxy, just turn it off in the settings.</li>
</ul>
<hr>
<p class=""muted"">Proxy settings live under Options &rarr; Network Preferences &rarr; Proxies.</p>";
        return MakePageShell("#000080", "Proxy Server Error", body);
    }

    /// <summary>
    /// Generate HTML for protocol not supported errors.
    /// </summary>
    public static string ProtocolNotSupported(string url, string protocol)
    {
        string escapedUrl = EscapeHtml(url);
        string escapedProtocol = EscapeHtml(protocol);
        string body = $@"
{WarningBanner("Protocol Not Supported", "Retro96 does not know how to handle this URL scheme.", "#cc0000", "#800000")}
<p>I'm not sure how to open this kind of address:</p>
<blockquote><code class=""code""><b>{escapedUrl}</b></code></blockquote>
<p>The <b><code class=""code"">{escapedProtocol}://</code></b> part of that link isn't something I know how to handle.</p>
<hr>
<p><b>Here's what I can handle:</b></p>
<ul>
  <li><code class=""code"">http://</code> &mdash; World Wide Web</li>
  <li><code class=""code"">https://</code> &mdash; Secure World Wide Web</li>
  <li><code class=""code"">ftp://</code> &mdash; File Transfer Protocol</li>
  <li><code class=""code"">file://</code> &mdash; Local files on your computer</li>
  <li><code class=""code"">mailto:</code> &mdash; Send electronic mail</li>
  <li><code class=""code"">news://</code> &mdash; Usenet newsgroups</li>
</ul>";
        return MakePageShell("#000080", "Protocol Not Supported", body);
    }

    /// <summary>
    /// Generate HTML for file not found errors (local file:// URLs).
    /// </summary>
    public static string LocalFileNotFound(string path)
    {
        string escapedPath = EscapeHtml(path);
        string body = $@"
{WarningBanner("File Not Found", "The local file could not be found on disk.", "#cc0000", "#800000")}
<p>I looked on your computer, but I couldn't find this file:</p>
<blockquote><code class=""code""><b>{escapedPath}</b></code></blockquote>
<p><b>A few things to check:</b></p>
<ul>
  <li>Make sure the file hasn't been moved, renamed, or deleted.</li>
  <li>Double-check the path &mdash; capitalization matters on some systems!</li>
  <li>If you clicked a bookmark, the file might have been moved since you saved it.</li>
</ul>
<hr>
<p class=""muted"">Local file access error: no such file or directory.</p>";
        return MakePageShell("#000080", "Local File Not Found", body);
    }

    /// <summary>
    /// Generate HTML for plugin or helper application required.
    /// </summary>
    public static string PluginRequired(string mimeType, string pluginName)
    {
        string escapedMime = EscapeHtml(mimeType);
        string escapedPlugin = EscapeHtml(pluginName);
        string body = $@"
{WarningBanner("Plugin Required", "You need extra software to view this content.", "#006600", "#004400")}
<p class=""lead"">To see this content, you'll need to install a little extra software.</p>
<table class=""details"">
<tr><td><b>Content type:</b></td><td><code class=""code"">{escapedMime}</code></td></tr>
<tr><td><b>You need:</b></td><td><b>{escapedPlugin}</b></td></tr>
</table>
<br>
<p>Just hop over to the plugin author's website, download it, and install it.
Once it's set up, <a href=""retro96://reload""><b>reload this page</b></a>.</p>
<hr>
<p class=""muted"">Retro96 supports Netscape-style plugins (.dll).</p>";
        return MakePageShell("#006600", "Plugin Required", body);
    }

    /// <summary>
    /// Generate HTML for unsupported features.
    /// </summary>
    public static string NotSupported(string feature)
    {
        string escapedFeature = EscapeHtml(feature);
        string body = $@"
{WarningBanner("Feature Not Supported", "Retro96 cannot handle this feature yet.", "#cc0000", "#800000")}
<p>I don't quite know how to handle this feature yet:</p>
<blockquote><b>{escapedFeature}</b></blockquote>
<p>This is probably because:</p>
<ul>
  <li>It needs a newer version of Retro96.</li>
  <li>I just haven't been programmed to do it yet.</li>
  <li>It needs a plugin that you don't have installed.</li>
</ul>
<hr>
<p class=""muted"">Retro96 aims to be compatible with Netscape Navigator 3.0.</p>";
        return MakePageShell("#000080", "Unsupported Feature", body);
    }

    /// <summary>
    /// Generate HTML for FTP errors.
    /// </summary>
    public static string FtpError(string url, string message)
    {
        string escapedUrl = EscapeHtml(url);
        string escapedMessage = EscapeHtml(message);
        string body = $@"
{WarningBanner("FTP Error", "The FTP server could not be contacted or returned an error.", "#cc0000", "#800000")}
<p class=""lead"">I ran into a snag while trying to connect to this FTP server:</p>
<blockquote><code class=""code""><b>{escapedUrl}</b></code></blockquote>
<p><b>The server said:</b> {escapedMessage}</p>
<hr>
<p><b>Things to check:</b></p>
<ul>
  <li>If the server needs a login, try putting your username and password in the address:<br>
    <code class=""code"">ftp://user:password@ftp.example.com/</code></li>
  <li>The server might not allow anonymous logins.</li>
  <li>Make sure the path you typed actually exists.</li>
  <li>The server might be full &mdash; try again in a little while.</li>
</ul>";
        return MakePageShell("#000080", "FTP Error", body);
    }

    /// <summary>
    /// Generate HTML for HTTP 400 Bad Request.
    /// </summary>
    public static string BadRequest(string url, string? detail = null)
    {
        string escapedUrl = EscapeHtml(url);
        string detailHtml = string.IsNullOrEmpty(detail)
            ? ""
            : $"<p><b>Message from server:</b> {EscapeHtml(detail!)}</p>";
        string body = $@"
{WarningBanner("400 \u2014 Bad Request", "The server could not understand the request.", "#cc0000", "#800000")}
<p>The server couldn't understand the request I sent for this address:</p>
<blockquote><code class=""code""><b>{escapedUrl}</b></code></blockquote>
{detailHtml}
<hr>
<p>This usually means the address was typed in a way the server refuses to
understand. Look out for weird characters and try again.</p>
<p class=""muted"">HTTP 400 Bad Request</p>";
        return MakePageShell("#000080", "400 \u2014 Bad Request", body);
    }

    /// <summary>
    /// Generate HTML for HTTP 401 Unauthorized — the shell shows its
    /// credentials prompt alongside this page.
    /// </summary>
    public static string Unauthorized(string url, string? realm = null)
    {
        string escapedUrl = EscapeHtml(url);
        string realmHtml = string.IsNullOrEmpty(realm)
            ? ""
            : $"<p>Restricted area: <b>{EscapeHtml(realm!)}</b></p>";
        string body = $@"
{WarningBanner("401 \u2014 Authorization Required", "This site requires a username and password.", "#cc9900", "#996600")}
<p class=""lead"">The server at this address is asking for a username and password before it
will let you in:</p>
<blockquote><code class=""code""><b>{escapedUrl}</b></code></blockquote>
{realmHtml}
<hr>
<p>Type your login details into the prompt, or hit <b>Cancel</b> to stay where
you are. If you think you shouldn't be seeing this, you might want to reach out
to the site's admin.</p>
<p class=""muted"">HTTP 401 Unauthorized &mdash; WWW-Authenticate: Basic</p>";
        return MakePageShell("#800000", "401 \u2014 Authorization Required", body);
    }

    /// <summary>
    /// Generate HTML for HTTP 503 Service Unavailable.
    /// </summary>
    public static string ServiceUnavailable(string url)
    {
        string escapedUrl = EscapeHtml(url);
        string body = $@"
{WarningBanner("503 \u2014 Service Unavailable", "The server is temporarily unavailable.", "#cc9900", "#996600")}
<p class=""lead"">The server is temporarily refusing to handle requests for this page:</p>
<blockquote><code class=""code""><b>{escapedUrl}</b></code></blockquote>
<p>Don't worry, this is usually temporary! The server might just be overwhelmed
with visitors or down for a quick tune-up.</p>
<hr>
<p><a href=""retro96://reload""><b>Reload this page</b></a> in a few minutes.</p>
<p class=""muted"">HTTP 503 Service Unavailable</p>";
        return MakePageShell("#800000", "503 \u2014 Service Unavailable", body);
    }

    /// <summary>
    /// Generate HTML for any other HTTP status code.
    /// </summary>
    public static string GenericHttpError(int statusCode, string url)
    {
        string escapedUrl = EscapeHtml(url);
        string statusText = $"HTTP {statusCode}";
        string body = $@"
{WarningBanner(statusText, "The server returned an unexpected status code.", "#cc0000", "#800000")}
<p class=""lead"">The server responded with an unexpected status code (<b>{statusCode}</b>) for:</p>
<blockquote><code class=""code""><b>{escapedUrl}</b></code></blockquote>
<hr>
<p>If the server included any extra info, it will be shown below:</p>";
        return MakePageShell("#000080", statusText, body);
    }

    /// <summary>
    /// Generate HTML for out-of-memory or resource exhaustion errors.
    /// </summary>
    public static string OutOfMemory()
    {
        string body = $@"
{WarningBanner("Out of Memory", "Retro96 has run out of memory and cannot continue.", "#cc0000", "#800000")}
<p>Retro96 could not complete the operation because there is not enough available memory.
The page may be unusually large or contain many images.</p>
<hr>
<p><b>Here's what you can do to fix it:</b></p>
<ul>
  <li>Close some of your other programs to free up memory.</li>
  <li>Close other browser windows or tabs you are not using.</li>
  <li>Clear out your disk cache: <b>Options &rarr; Network Preferences &rarr; Clear Cache</b></li>
  <li>Restart Retro96.</li>
  <li>If this keeps happening, your computer may need more memory.</li>
</ul>
<p class=""muted"">For smooth browsing, Retro96 likes to have at least 8 MB of RAM to play with.</p>";
        return MakePageShell("#800000", "Out of Memory", body);
    }
}