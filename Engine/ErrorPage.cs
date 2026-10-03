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
<hr noshade size=""3"" color=""#000080"">
<table width=""100%"" cellpadding=""0"" cellspacing=""0"">
<tr>
  <td><font face=""Arial,Helvetica"" size=""-2"" color=""#808080"">
    Retro96 Browser &mdash; Netscape Navigator 3.0 Compatible<br>
    &copy; 2026 Retro96 Project. All rights reserved.
  </font></td>
  <td align=""right"" valign=""top"">
    <font face=""Arial,Helvetica"" size=""-2"" color=""#808080"">
      [<a href=""retro96://home"">Home</a>] &nbsp;
      [<a href=""retro96://back"">Back</a>] &nbsp;
      [<a href=""retro96://reload"">Reload</a>]
    </font>
  </td>
</tr>
</table>";

    private static string MakePageShell(string titleColor, string titleText, string bodyContent) => $@"<html>
<head><title>{EscapeHtml(titleText)}</title></head>
<body bgcolor=""#c0c0c0"" text=""#000000"" link=""#0000ff"" vlink=""#800080"">
<table width=""100%"" height=""100%"" border=""0"" cellpadding=""24"" cellspacing=""0"">
<tr><td align=""center"" valign=""middle"">
<table bgcolor=""#ffffff"" border=""3"" cellpadding=""0"" cellspacing=""0"" width=""520"" style=""border-style:outset"">
  <tr><td bgcolor=""{titleColor}"" cellpadding=""8"">
    <table width=""100%"" cellpadding=""6"" cellspacing=""0"">
    <tr>
      <td><font color=""#ffffff"" size=""+2"" face=""Arial,Helvetica""><b>{EscapeHtml(titleText)}</b></font></td>
      <td align=""right""><font color=""#c0c0c0"" size=""-1"" face=""Arial,Helvetica"">Retro96</font></td>
    </tr>
    </table>
  </td></tr>
  <tr><td cellpadding=""14"">
    <font face=""Arial,Helvetica"">
{bodyContent}
{Footer}
    </font>
  </td></tr>
</table>
</td></tr>
</table>
</body>
</html>";

    /// <summary>
    /// Generate HTML for network errors (connection failed, DNS failure, etc.)
    /// </summary>
    public static string NetworkError(string url, string message)
    {
        string escapedUrl = EscapeHtml(url);
        string escapedMessage = EscapeHtml(message);
        string body = $@"
<h2><font color=""#cc0000"">&#9888; Unable to Connect to Server</font></h2>
<p>Retro96 was unable to establish a connection to the following location:</p>
<blockquote><tt><b>{escapedUrl}</b></tt></blockquote>
<p><b>Technical reason:</b> {escapedMessage}</p>
<hr noshade color=""#c0c0c0"">
<p><b>Things you can try:</b></p>
<ul>
  <li>Check that you typed the address correctly</li>
  <li>Make sure your Wi-Fi or Ethernet connection is active</li>
  <li>Try pressing <b>Reload</b> &mdash; the problem may be temporary</li>
  <li>Check your router or contact your network administrator if the problem persists</li>
  <li>The remote server may be down for maintenance</li>
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
<h2><font color=""#cc0000"">&#9888; Server Not Found</font></h2>
<marquee behavior=""scroll"" direction=""left"" scrollamount=""3"" bgcolor=""#ffff00"">
  <font color=""#000000"" size=""-1""><b>&nbsp;DNS lookup failed &mdash; host could not be resolved &nbsp;</b></font>
</marquee>
<br>
<p>The hostname <b><tt>{escapedHost}</tt></b> could not be found in the Domain Name System (DNS).</p>
<p>This usually means one of the following:</p>
<ul>
  <li>The domain name was typed incorrectly &mdash; check for typos</li>
  <li>The domain does not exist or has expired</li>
  <li>Your DNS server is not responding &mdash; try again later</li>
  <li>Your network connection is not active</li>
</ul>
<hr noshade color=""#c0c0c0"">
<p><font color=""#808080"" size=""-1"">DNS Error &mdash; No address associated with hostname</font></p>";
        return MakePageShell("#800000", "DNS Failure", body);
    }

    /// <summary>
    /// Generate HTML for certificate errors.
    /// Includes an "Accept Risk" button to invoke the onAcceptRisk callback.
    /// </summary>
    public static string CertificateError(string url, string message, Func<bool> onAcceptRisk)
    {
        string escapedUrl = EscapeHtml(url);
        string escapedMessage = EscapeHtml(message);
        string body = $@"
<h2><font color=""#cc0000"">&#128274; Security Warning</font></h2>
<table bgcolor=""#ffffcc"" border=""1"" bordercolor=""#cc9900"" cellpadding=""8"" width=""100%"">
<tr><td>
  <font color=""#996600"">
  <b>WARNING:</b> This site's security certificate cannot be verified.<br>
  Proceeding may expose your personal information to risk.
  </font>
</td></tr>
</table>
<br>
<p><b>URL:</b> <tt>{escapedUrl}</tt></p>
<p><b>Certificate problem:</b> {escapedMessage}</p>
<p>A valid certificate proves you are talking to the real server and that
information sent between you and the server is encrypted and cannot be
read by others.</p>
<hr noshade color=""#c0c0c0"">
<p>Do you wish to proceed anyway?</p>
<form onsubmit=""if(confirm('WARNING: You are about to accept an untrusted certificate.\n\nProceed only if you understand the risks.\n\nContinue?')) {{ window.acceptCertRisk && window.acceptCertRisk(); }} return false;"">
  <input type=""submit"" value=""  Accept Risk and Continue  "">&nbsp;&nbsp;
  <input type=""button"" value=""  Go Back (Recommended)  "" onclick=""history.back()"">
</form>
<p><font color=""#808080"" size=""-1"">Retro96 has blocked this page to protect your security.</font></p>";
        return MakePageShell("#800000", "Security Warning \u2014 Certificate Error", body);
    }

    /// <summary>
    /// Generate HTML for connection timeout errors.
    /// </summary>
    public static string Timeout(string url)
    {
        string escapedUrl = EscapeHtml(url);
        string body = $@"
<h2><font color=""#cc0000"">&#9201; Connection Timed Out</font></h2>
<p>The server at <b><tt>{escapedUrl}</tt></b> is taking too long to respond.</p>
<p>The connection attempt was abandoned after waiting the maximum allowed time.</p>
<hr noshade color=""#c0c0c0"">
<p><b>Possible causes:</b></p>
<ul>
  <li>The server may be overloaded with requests &mdash; try again shortly</li>
  <li>Your connection may be slow or intermittent</li>
  <li>A firewall may be blocking the connection</li>
  <li>The server may have crashed or gone offline</li>
</ul>
<p><a href=""retro96://reload"">&#8635; Try Again</a></p>";
        return MakePageShell("#000080", "Connection Timed Out", body);
    }

    /// <summary>
    /// Generate HTML for too many redirects.
    /// </summary>
    public static string TooManyRedirects(string url)
    {
        string escapedUrl = EscapeHtml(url);
        string body = $@"
<h2><font color=""#cc0000"">&#8635; Redirect Loop Detected</font></h2>
<p>Retro96 has stopped trying to load this page because it appears to be
stuck in an infinite redirect loop.</p>
<blockquote><tt><b>{escapedUrl}</b></tt></blockquote>
<p>The page redirected more than <b>5 times</b>. This usually indicates a
misconfiguration on the web server.</p>
<hr noshade color=""#c0c0c0"">
<p><b>This is not a problem with Retro96.</b> Please contact the website
administrator if the problem continues.</p>";
        return MakePageShell("#000080", "Too Many Redirects", body);
    }

    /// <summary>
    /// Generate HTML for 404 Not Found errors.
    /// </summary>
    public static string NotFound(string url)
    {
        string escapedUrl = EscapeHtml(url);
        string body = $@"
<h2><font color=""#cc0000"">404 &mdash; Page Not Found</font></h2>
<marquee behavior=""alternate"" scrollamount=""2"">
  <font color=""#000080"" size=""-1""><b>The requested page could not be located on this server.</b></font>
</marquee>
<br>
<p>The page or file you requested does not exist:</p>
<blockquote><tt><b>{escapedUrl}</b></tt></blockquote>
<hr noshade color=""#c0c0c0"">
<p><b>Suggestions:</b></p>
<ul>
  <li>Check the URL for spelling mistakes &mdash; URLs are case-sensitive</li>
  <li>The page may have been moved or deleted</li>
  <li>Try the site's home page and navigate from there</li>
  <li>Use a search engine to look for the page</li>
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
<h2><font color=""#cc0000"">&#128274; Access Denied</font></h2>
<table bgcolor=""#ffcccc"" border=""1"" bordercolor=""#cc0000"" cellpadding=""8"" width=""100%"">
<tr><td>
  <font color=""#800000"">
  You do not have permission to access this resource.
  </font>
</td></tr>
</table>
<br>
<p><b>URL:</b> <tt>{escapedUrl}</tt></p>
<p>The server understood your request but is refusing to fulfil it.
This may be because:</p>
<ul>
  <li>You need to log in to access this area</li>
  <li>Your account does not have sufficient privileges</li>
  <li>Access to this resource is restricted by IP address</li>
  <li>The administrator has disabled access to this page</li>
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
            : $"<p><b>Server message:</b> {EscapeHtml(detail!)}</p>";
        string body = $@"
<h2><font color=""#cc0000"">&#9888; Internal Server Error</font></h2>
<p>The web server encountered an unexpected condition that prevented it
from fulfilling your request.</p>
<blockquote><tt><b>{escapedUrl}</b></tt></blockquote>
{detailHtml}
<hr noshade color=""#c0c0c0"">
<p>This is a problem with the <b>remote server</b>, not with Retro96.</p>
<ul>
  <li>The server-side script or application has crashed</li>
  <li>Try again in a few minutes &mdash; the server may be restarting</li>
  <li>Contact the website administrator if the problem persists</li>
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
<h2><font color=""#cc0000"">&#9888; Invalid Address</font></h2>
<p>Retro96 cannot load the following address because it appears to be invalid:</p>
<blockquote><tt><b>{escapedUrl}</b></tt></blockquote>
<p><b>Common mistakes:</b></p>
<ul>
  <li>Missing <tt>http://</tt> at the start of the address</li>
  <li>Spaces in the URL &mdash; use <tt>%20</tt> instead of spaces</li>
  <li>Invalid characters in the URL</li>
  <li>Double slashes or missing slashes after the domain</li>
</ul>
<hr noshade color=""#c0c0c0"">
<p><font color=""#808080"" size=""-1"">A valid URL looks like: <tt>http://www.example.com/page.html</tt></font></p>";
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
<h2><font color=""#cc0000"">&#9888; Proxy Server Error</font></h2>
<p>Retro96 is configured to use a proxy server, but the proxy could not
be reached or returned an error.</p>
<table cellpadding=""4"" cellspacing=""0"" bgcolor=""#f0f0f0"" border=""1"" bordercolor=""#c0c0c0"" width=""100%"">
<tr><td width=""120""><b>Proxy server:</b></td><td><tt>{escapedProxy}</tt></td></tr>
<tr><td><b>Error:</b></td><td>{escapedMessage}</td></tr>
</table>
<br>
<p><b>What to do:</b></p>
<ul>
  <li>Check your proxy settings in <b>Options &rarr; Network Preferences</b></li>
  <li>Contact your network administrator or ISP</li>
  <li>If you do not need a proxy, disable it in Network Preferences</li>
</ul>
<hr noshade color=""#c0c0c0"">
<p><font color=""#808080"" size=""-1"">Proxy configuration can be changed under Options &rarr; Network Preferences &rarr; Proxies.</font></p>";
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
<h2><font color=""#cc0000"">&#9888; Protocol Not Supported</font></h2>
<p>Retro96 does not know how to handle the following type of address:</p>
<blockquote><tt><b>{escapedUrl}</b></tt></blockquote>
<p>The protocol <b><tt>{escapedProtocol}://</tt></b> is not supported by this browser.</p>
<hr noshade color=""#c0c0c0"">
<p><b>Retro96 supports the following protocols:</b></p>
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
<h2><font color=""#cc0000"">File Not Found</font></h2>
<p>The file you requested cannot be found on your computer:</p>
<blockquote><tt><b>{escapedPath}</b></tt></blockquote>
<p><b>Suggestions:</b></p>
<ul>
  <li>Make sure the file has not been moved, renamed or deleted</li>
  <li>Check that the path and filename are correct &mdash; they are case-sensitive on some systems</li>
  <li>If you are opening a bookmark, the file may have been moved since it was saved</li>
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
<h2><font color=""#cc0000"">Plugin Required</font></h2>
<marquee behavior=""scroll"" direction=""left"" scrollamount=""2"" bgcolor=""#ccffcc"">
  <font color=""#004400"" size=""-1""><b>&nbsp;&#9733; Additional software is needed to view this content &#9733;&nbsp;</b></font>
</marquee>
<br>
<p>To view this content, you need to install a plugin or helper application.</p>
<table cellpadding=""6"" cellspacing=""0"" bgcolor=""#f0f0f0"" border=""1"" bordercolor=""#c0c0c0"" width=""100%"">
<tr><td width=""140""><b>Content type:</b></td><td><tt>{escapedMime}</tt></td></tr>
<tr><td><b>Required plugin:</b></td><td><b>{escapedPlugin}</b></td></tr>
</table>
<br>
<p>Visit the plugin author's website to download and install the required software.
Once installed, <a href=""retro96://reload"">reload this page</a>.</p>
<hr noshade color=""#c0c0c0"">
<p><font color=""#808080"" size=""-1"">Retro96 supports Netscape-compatible plugins (.dll).</font></p>";
        return MakePageShell("#006600", "Plugin Required", body);
    }

    /// <summary>
    /// Generate HTML for unsupported features.
    /// </summary>
    public static string NotSupported(string feature)
    {
        string escapedFeature = EscapeHtml(feature);
        string body = $@"
<h2><font color=""#cc0000"">Feature Not Supported</font></h2>
<p>Retro96 does not currently support the following feature:</p>
<blockquote><b>{escapedFeature}</b></blockquote>
<p>This may be because:</p>
<ul>
  <li>The feature requires a newer version of Retro96</li>
  <li>The feature is experimental and has not yet been implemented</li>
  <li>The feature requires a plugin that is not installed</li>
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
<h2><font color=""#cc0000"">FTP Error</font></h2>
<p>An error occurred while connecting to the FTP server:</p>
<blockquote><tt><b>{escapedUrl}</b></tt></blockquote>
<p><b>Server response:</b> {escapedMessage}</p>
<hr noshade color=""#c0c0c0"">
<p><b>Things to check:</b></p>
<ul>
  <li>If the server requires a login, include credentials in the URL:<br>
    <tt>ftp://username:password@ftp.example.com/</tt></li>
  <li>Anonymous FTP may not be available on this server</li>
  <li>Check that the remote path exists and you have read permission</li>
  <li>The server may be at capacity &mdash; try again later</li>
</ul>";
        return MakePageShell("#000080", "FTP Error", body);
    }

    /// <summary>
    /// Generate HTML for out-of-memory or resource exhaustion errors.
    /// </summary>
    public static string OutOfMemory()
    {
        string body = @"
<h2><font color=""#cc0000"">&#9888; Out of Memory</font></h2>
<blink><font color=""#ff0000""><b>CRITICAL: Retro96 has run out of available memory.</b></font></blink>
<br><br>
<p>There is not enough free memory to complete this operation.
The page may be extremely large or contain many images.</p>
<hr noshade color=""#c0c0c0"">
<p><b>Suggested actions:</b></p>
<ul>
  <li>Close other running programs to free up memory</li>
  <li>Close unused browser windows and tabs</li>
  <li>Clear the disk cache: <b>Options &rarr; Network Preferences &rarr; Clear Cache</b></li>
  <li>Restart Retro96</li>
  <li>Add more RAM to your computer (minimum 4 GB recommended, 8 GB for best performance)</li>
</ul>
<p><font color=""#808080"" size=""-1"">Retro96 recommends at least 8 MB of RAM for smooth browsing.</font></p>";
        return MakePageShell("#800000", "Out of Memory", body);
    }
}