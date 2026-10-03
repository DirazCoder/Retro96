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
<table width=""100%"" cellpadding=""0"" cellspacing=""0"" border=""0"">
<tr>
  <td align=""left""><font face=""Arial,Helvetica"" size=""-1"" color=""#808080"">
    &copy; DirazCoder 2026
  </font></td>
  <td align=""right""><font face=""Arial,Helvetica"" size=""-1"" color=""#808080"">
    <a href=""retro96://home"">Home</a>
  </font></td>
</tr>
</table>";

    // NOTE: The page uses a 100%x100% table to perfectly vertically and 
    // horizontally centre the compact dialog using nested tables for a
    // 1996-era raised-window appearance.
    //
    // WARNING: All emoji and astral-plane symbols (e.g. &#9888; &#9201; &#8635;
    // &#9733;) have been removed. They postdate the Unicode 1.1 / 2.0 era and 
    // cannot survive a naive (char)code entity decode in 1996 browsers, so 
    // the error pages now use only plain ASCII text, <blink>, <marquee>, and 
    // coloured <font> tags to get that authentic 1996 look.
    private static string MakePageShell(string titleColor, string titleText, string bodyContent) => $@"<html>
<head><title>{EscapeHtml(titleText)}</title></head>
<body bgcolor=""#c0c0c0"" text=""#000000"" link=""#0000ff"" vlink=""#800080"">
<table width=""100%"" height=""100%"" border=""0"" cellpadding=""0"" cellspacing=""0"">
<tr>
  <td align=""center"" valign=""middle"">
    
    <table bgcolor=""#c0c0c0"" border=""2"" cellpadding=""1"" cellspacing=""0"" width=""640"" style=""border-style:outset"">
    <tr><td>
      <table bgcolor=""#ffffff"" border=""2"" cellpadding=""0"" cellspacing=""0"" width=""100%"" style=""border-style:inset"">
        
        <tr><td bgcolor=""{titleColor}"">
          <table width=""100%"" cellpadding=""6"" cellspacing=""0"" border=""0"">
          <tr>
            <td align=""left""><font color=""#ffffff"" size=""+2"" face=""Arial,Helvetica""><b>{EscapeHtml(titleText)}</b></font></td>
            <td align=""right""><font color=""#c0c0c0"" size=""-1"" face=""Arial,Helvetica""><b>Retro96</b></font></td>
          </tr>
          </table>
        </td></tr>
        
        <tr><td>
          <table width=""100%"" cellpadding=""16"" cellspacing=""0"" border=""0"">
          <tr><td align=""left"" valign=""top"">
          <font face=""Arial,Helvetica"" size=""+1"">
{bodyContent}
<br>
{Footer}
          </font>
          </td></tr>
          </table>
        </td></tr>
        
      </table>
    </td></tr>
    </table>
    
  </td>
</tr>
</table>
</body>
</html>";

    // Big, friendly 1996-style warning banner used at the top of each error.
    private static string WarningBanner(string title, string subtitle, string bg, string border, string fg) => $@"
<table bgcolor=""{bg}"" border=""2"" bordercolor=""{border}"" cellpadding=""8"" cellspacing=""0"" width=""100%"" style=""border-style:outset"">
<tr><td>
  <table cellpadding=""0"" cellspacing=""0"" border=""0"">
  <tr>
    <td width=""28"" align=""center""><font color=""{fg}"" size=""+3"" face=""Arial,Helvetica""><b>!</b></font></td>
    <td>
      <font color=""{fg}"" face=""Arial,Helvetica""><b><span style=""font-size:larger"">{EscapeHtml(title)}</span></b></font><br>
      <font color=""{fg}"" size=""+1"" face=""Arial,Helvetica"">{EscapeHtml(subtitle)}</font>
    </td>
  </tr>
  </table>
</td></tr>
</table>
<br>";

    /// <summary>
    /// Generate HTML for network errors (connection failed, DNS failure, etc.)
    /// </summary>
    public static string NetworkError(string url, string message)
    {
        string escapedUrl = EscapeHtml(url);
        string escapedMessage = EscapeHtml(message);
        string body = $@"
{WarningBanner("Unable to Connect", "A network connection could not be established.", "#ffcccc", "#cc0000", "#800000")}
<font size=""+2""><b>The address I tried to reach:</b></font>
<blockquote><tt><b>{escapedUrl}</b></tt></blockquote>
<p><b>The technical reason:</b> {escapedMessage}</p>
<hr noshade color=""#c0c0c0"">
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
{WarningBanner("Server Not Found", "The host name could not be resolved by DNS.", "#ffffcc", "#cc9900", "#996600")}
<marquee behavior=""scroll"" direction=""left"" scrollamount=""3"" bgcolor=""#ffff00"">
  <font color=""#000000"" size=""+1""><b>&nbsp;&nbsp;Looking for the server... no luck! &nbsp;&nbsp;</b></font>
</marquee>
<br><br>
<p><font size=""+2"">I looked, but I couldn't find <b><tt>{escapedHost}</tt></b> on the network.</font></p>
<p><b>This usually means one of a few things:</b></p>
<ul>
  <li>You might have accidentally mistyped the address.</li>
  <li>The website might not exist anymore, or the domain expired.</li>
  <li>Your computer's DNS settings might be acting up.</li>
  <li>Your internet connection might have dropped.</li>
</ul>
<hr noshade color=""#c0c0c0"">
<p><font color=""#808080"" size=""-1"">DNS Error &mdash; The hostname could not be resolved.</font></p>";
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
{WarningBanner("Security Warning", "This site's certificate could not be verified.", "#ffffcc", "#cc9900", "#996600")}
<p><b><font color=""#996600"" size=""+1"">Heads up!</font></b> This site's security certificate couldn't be verified.
Proceeding could expose your personal info to prying eyes.</p>
<hr noshade color=""#c0c0c0"">
<p><b>URL:</b> <tt>{escapedUrl}</tt></p>
<p><b>What went wrong:</b> {escapedMessage}</p>
<p>A valid certificate is how a website proves it is who it claims to be.
Without one, your connection isn't secure, and someone else might be able to
read the information you send.</p>
<hr noshade color=""#c0c0c0"">
<p><b><font size=""+1"">Are you sure you want to continue anyway?</font></b></p>
<form onsubmit=""if(confirm('WARNING: You are about to accept an untrusted certificate.\n\nProceed only if you understand the risks.\n\nContinue?')) {{ window.acceptCertRisk && window.acceptCertRisk(); }} return false;"">
  <input type=""submit"" value=""  Accept Risk and Continue  "">&nbsp;&nbsp;
  <input type=""button"" value=""  Go Back (Recommended)  "" onclick=""history.back()"">
</form>
<p><font color=""#808080"" size=""-1"">I've blocked this page to keep you safe.</font></p>";
        return MakePageShell("#800000", "Security Warning \u2014 Certificate Error", body);
    }

    /// <summary>
    /// Generate HTML for connection timeout errors.
    /// </summary>
    public static string Timeout(string url)
    {
        string escapedUrl = EscapeHtml(url);
        string body = $@"
{WarningBanner("Connection Timed Out", "The server did not respond in time.", "#ffcccc", "#cc0000", "#800000")}
<p><font size=""+2"">I tried reaching <b><tt>{escapedUrl}</tt></b>, but it's taking way too long to respond.</font></p>
<p>I waited as long as I could, but eventually had to give up.</p>
<hr noshade color=""#c0c0c0"">
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
{WarningBanner("Redirect Loop Detected", "The page redirected too many times.", "#ffcccc", "#cc0000", "#800000")}
<p><font size=""+2"">I had to stop trying to load this page because it seems to be stuck in an
infinite loop, bouncing me around without ever showing the actual content.</font></p>
<blockquote><tt><b>{escapedUrl}</b></tt></blockquote>
<p>The page redirected more than <b>5 times</b>. This is almost always a mistake
on the website's end.</p>
<hr noshade color=""#c0c0c0"">
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
{WarningBanner("404 \u2014 Page Not Found", "The requested URL could not be found on the server.", "#ffcccc", "#cc0000", "#800000")}
<marquee behavior=""alternate"" scrollamount=""2"">
  <font color=""#000080"" size=""+1""><b>Sorry, I looked everywhere but couldn't find that page!</b></font>
</marquee>
<br><br>
<p>The page or file you're looking for doesn't seem to exist:</p>
<blockquote><tt><b>{escapedUrl}</b></tt></blockquote>
<hr noshade color=""#c0c0c0"">
<p><b>Here are some ideas:</b></p>
<ul>
  <li>Double-check the address for typos &mdash; web addresses are case-sensitive.</li>
  <li>The page might have been moved or deleted recently.</li>
  <li>Try going to the website's home page and navigating from there.</li>
</ul>
<p><font color=""#808080"" size=""-1"">HTTP 404 Not Found</font></p>";
        return MakePageShell("#000080", "404 \u2014 Page Not Found", body);
    }

    /// <summary>
    /// Generate HTML for 403 Access Denied errors.
    /// </summary>
    public static string AccessDenied(string url)
    {
        string escapedUrl = EscapeHtml(url);
        string body = $@"
{WarningBanner("403 \u2014 Access Denied", "You do not have permission to view this page.", "#ffcccc", "#cc0000", "#800000")}
<p>Sorry, you don't have permission to view this page.</p>
<p><b>URL:</b> <tt>{escapedUrl}</tt></p>
<p>The server understood the request, but it's refusing to show you anything. This could be because:</p>
<ul>
  <li>You need to log in to see this page.</li>
  <li>Your account doesn't have the right privileges.</li>
  <li>The website is blocking your IP address.</li>
  <li>The owner has locked this page down completely.</li>
</ul>
<hr noshade color=""#c0c0c0"">
<p><font color=""#808080"" size=""-1"">HTTP 403 Forbidden</font></p>";
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
{WarningBanner("500 \u2014 Internal Server Error", "Something went wrong on the server.", "#ffcccc", "#cc0000", "#800000")}
<p><font size=""+2"">Something went wrong on the website's end, and it couldn't finish loading
the page you wanted.</font></p>
<blockquote><tt><b>{escapedUrl}</b></tt></blockquote>
{detailHtml}
<hr noshade color=""#c0c0c0"">
<p><b>This isn't a problem with Retro96.</b> The site's code likely crashed.</p>
<ul>
  <li>Try refreshing in a few minutes &mdash; they might be restarting things.</li>
  <li>If it keeps happening, you might want to contact the site's admin.</li>
</ul>
<p><font color=""#808080"" size=""-1"">HTTP 500 Internal Server Error</font></p>";
        return MakePageShell("#800000", "500 \u2014 Internal Server Error", body);
    }

    /// <summary>
    /// Generate HTML for malformed or invalid URLs.
    /// </summary>
    public static string MalformedUrl(string url)
    {
        string escapedUrl = EscapeHtml(url);
        string body = $@"
{WarningBanner("Invalid Address", "The address you entered could not be parsed.", "#ffcccc", "#cc0000", "#800000")}
<p>I can't open this address because it doesn't look quite right:</p>
<blockquote><tt><b>{escapedUrl}</b></tt></blockquote>
<p><b>Common mistakes to look out for:</b></p>
<ul>
  <li>Forgetting the <tt>http://</tt> at the very beginning.</li>
  <li>Using spaces instead of <tt>%20</tt>.</li>
  <li>Accidentally including strange characters.</li>
  <li>Mixing up the slashes after the domain name.</li>
</ul>
<hr noshade color=""#c0c0c0"">
<p><font color=""#808080"" size=""-1"">A valid address looks like: <tt>http://www.example.com/page.html</tt></font></p>";
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
{WarningBanner("Proxy Server Error", "The proxy server could not be reached or returned an error.", "#ffcccc", "#cc0000", "#800000")}
<p>You're set up to use a proxy server, but I couldn't reach it or it sent back an error.</p>
<table cellpadding=""6"" cellspacing=""0"" bgcolor=""#f0f0f0"" border=""1"" bordercolor=""#c0c0c0"" width=""100%"">
<tr><td width=""120""><b>Proxy server:</b></td><td><tt>{escapedProxy}</tt></td></tr>
<tr><td><b>Error:</b></td><td>{escapedMessage}</td></tr>
</table>
<br>
<p><b>What you can do:</b></p>
<ul>
  <li>Check your proxy settings under <b>Options &rarr; Network Preferences</b>.</li>
  <li>Get in touch with your network admin or ISP.</li>
  <li>If you don't actually need a proxy, just turn it off in the settings.</li>
</ul>
<hr noshade color=""#c0c0c0"">
<p><font color=""#808080"" size=""-1"">Proxy settings live under Options &rarr; Network Preferences &rarr; Proxies.</font></p>";
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
{WarningBanner("Protocol Not Supported", "Retro96 does not know how to handle this URL scheme.", "#ffcccc", "#cc0000", "#800000")}
<p>I'm not sure how to open this kind of address:</p>
<blockquote><tt><b>{escapedUrl}</b></tt></blockquote>
<p>The <b><tt>{escapedProtocol}://</tt></b> part of that link isn't something I know how to handle.</p>
<hr noshade color=""#c0c0c0"">
<p><b>Here's what I can handle:</b></p>
<ul>
  <li><tt>http://</tt> &mdash; World Wide Web</li>
  <li><tt>https://</tt> &mdash; Secure World Wide Web</li>
  <li><tt>ftp://</tt> &mdash; File Transfer Protocol</li>
  <li><tt>file://</tt> &mdash; Local files on your computer</li>
  <li><tt>mailto:</tt> &mdash; Send electronic mail</li>
  <li><tt>news://</tt> &mdash; Usenet newsgroups</li>
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
{WarningBanner("File Not Found", "The local file could not be found on disk.", "#ffcccc", "#cc0000", "#800000")}
<p>I looked on your computer, but I couldn't find this file:</p>
<blockquote><tt><b>{escapedPath}</b></tt></blockquote>
<p><b>A few things to check:</b></p>
<ul>
  <li>Make sure the file hasn't been moved, renamed, or deleted.</li>
  <li>Double-check the path &mdash; capitalization matters on some systems!</li>
  <li>If you clicked a bookmark, the file might have been moved since you saved it.</li>
</ul>
<hr noshade color=""#c0c0c0"">
<p><font color=""#808080"" size=""-1"">Local file access error: no such file or directory.</font></p>";
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
{WarningBanner("Plugin Required", "You need extra software to view this content.", "#ccffcc", "#006600", "#004400")}
<marquee behavior=""scroll"" direction=""left"" scrollamount=""2"" bgcolor=""#ccffcc"">
  <font color=""#004400"" size=""+1""><b>&nbsp;&nbsp;You need an extra piece of software to view this&nbsp;&nbsp;</b></font>
</marquee>
<br><br>
<p><font size=""+1"">To see this content, you'll need to install a little extra software.</font></p>
<table cellpadding=""8"" cellspacing=""0"" bgcolor=""#f0f0f0"" border=""1"" bordercolor=""#c0c0c0"" width=""100%"">
<tr><td width=""140""><b>Content type:</b></td><td><tt>{escapedMime}</tt></td></tr>
<tr><td><b>You need:</b></td><td><b>{escapedPlugin}</b></td></tr>
</table>
<br>
<p>Just hop over to the plugin author's website, download it, and install it.
Once it's set up, <a href=""retro96://reload""><b>reload this page</b></a>.</p>
<hr noshade color=""#c0c0c0"">
<p><font color=""#808080"" size=""-1"">Retro96 supports Netscape-style plugins (.dll).</font></p>";
        return MakePageShell("#006600", "Plugin Required", body);
    }

    /// <summary>
    /// Generate HTML for unsupported features.
    /// </summary>
    public static string NotSupported(string feature)
    {
        string escapedFeature = EscapeHtml(feature);
        string body = $@"
{WarningBanner("Feature Not Supported", "Retro96 cannot handle this feature yet.", "#ffcccc", "#cc0000", "#800000")}
<p>I don't quite know how to handle this feature yet:</p>
<blockquote><b>{escapedFeature}</b></blockquote>
<p>This is probably because:</p>
<ul>
  <li>It needs a newer version of Retro96.</li>
  <li>I just haven't been programmed to do it yet.</li>
  <li>It needs a plugin that you don't have installed.</li>
</ul>
<hr noshade color=""#c0c0c0"">
<p><font color=""#808080"" size=""-1"">Retro96 aims to be compatible with Netscape Navigator 3.0.</font></p>";
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
{WarningBanner("FTP Error", "The FTP server could not be contacted or returned an error.", "#ffcccc", "#cc0000", "#800000")}
<p><font size=""+1"">I ran into a snag while trying to connect to this FTP server:</font></p>
<blockquote><tt><b>{escapedUrl}</b></tt></blockquote>
<p><b>The server said:</b> {escapedMessage}</p>
<hr noshade color=""#c0c0c0"">
<p><b>Things to check:</b></p>
<ul>
  <li>If the server needs a login, try putting your username and password in the address:<br>
    <tt>ftp://username:password@ftp.example.com/</tt></li>
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
{WarningBanner("400 \u2014 Bad Request", "The server could not understand the request.", "#ffcccc", "#cc0000", "#800000")}
<p>The server couldn't understand the request I sent for this address:</p>
<blockquote><tt><b>{escapedUrl}</b></tt></blockquote>
{detailHtml}
<hr noshade color=""#c0c0c0"">
<p>This usually means the address was typed in a way the server refuses to
understand. Look out for weird characters and try again.</p>
<p><font color=""#808080"" size=""-1"">HTTP 400 Bad Request</font></p>";
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
{WarningBanner("401 \u2014 Authorization Required", "This site requires a username and password.", "#ffffcc", "#cc9900", "#996600")}
<p><font size=""+1"">The server at this address is asking for a username and password before it
will let you in:</font></p>
<blockquote><tt><b>{escapedUrl}</b></tt></blockquote>
{realmHtml}
<hr noshade color=""#c0c0c0"">
<p>Type your login details into the prompt, or hit <b>Cancel</b> to stay where
you are. If you think you shouldn't be seeing this, you might want to reach out
to the site's admin.</p>
<p><font color=""#808080"" size=""-1"">HTTP 401 Unauthorized &mdash; WWW-Authenticate: Basic</font></p>";
        return MakePageShell("#800000", "401 \u2014 Authorization Required", body);
    }

    /// <summary>
    /// Generate HTML for HTTP 503 Service Unavailable.
    /// </summary>
    public static string ServiceUnavailable(string url)
    {
        string escapedUrl = EscapeHtml(url);
        string body = $@"
{WarningBanner("503 \u2014 Service Unavailable", "The server is temporarily unavailable.", "#ffffcc", "#cc9900", "#996600")}
<p><font size=""+1"">The server is temporarily refusing to handle requests for this page:</font></p>
<blockquote><tt><b>{escapedUrl}</b></tt></blockquote>
<p>Don't worry, this is usually temporary! The server might just be overwhelmed
with visitors or down for a quick tune-up.</p>
<hr noshade color=""#c0c0c0"">
<p><a href=""retro96://reload""><b>Reload this page</b></a> in a few minutes.</p>
<p><font color=""#808080"" size=""-1"">HTTP 503 Service Unavailable</font></p>";
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
{WarningBanner(statusText, "The server returned an unexpected status code.", "#ffcccc", "#cc0000", "#800000")}
<p><font size=""+1"">The server responded with an unexpected status code (<b>{statusCode}</b>) for:</font></p>
<blockquote><tt><b>{escapedUrl}</b></tt></blockquote>
<hr noshade color=""#c0c0c0"">
<p>If the server included any extra info, it will be shown below:</p>";
        return MakePageShell("#000080", statusText, body);
    }

    /// <summary>
    /// Generate HTML for out-of-memory or resource exhaustion errors.
    /// </summary>
    public static string OutOfMemory()
    {
        string body = $@"
{WarningBanner("Out of Memory", "Retro96 has run out of memory and cannot continue.", "#ffcccc", "#cc0000", "#800000")}
<blink><font color=""#ff0000"" size=""+2""><b>Yikes! Retro96 has completely run out of memory.</b></font></blink>
<br><br>
<p><font size=""+1"">I tried to finish what I was doing, but there just isn't enough free memory
left to complete the operation. The page might be massive, or it might have
way too many images.</font></p>
<hr noshade color=""#c0c0c0"">
<p><b>Here's what you can do to fix it:</b></p>
<ul>
  <li>Close some of your other programs to free up memory.</li>
  <li>Shut any browser windows or tabs you aren't using anymore.</li>
  <li>Clear out your disk cache: <b>Options &rarr; Network Preferences &rarr; Clear Cache</b></li>
  <li>Restart Retro96.</li>
  <li>If this keeps happening, you might literally need to upgrade your computer's RAM!</li>
</ul>
<p><font color=""#808080"" size=""-1"">For smooth browsing, Retro96 likes to have at least 8 MB of RAM to play with.</font></p>";
        return MakePageShell("#800000", "Out of Memory", body);
    }
}