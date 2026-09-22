using System;
using System.Linq;
using Retro96.Engine.Dom;
using Retro96.Engine.Html;
using Retro96.Engine.Network;

public static class DebugDump
{
    public static void Run()
    {
        string html = System.IO.File.ReadAllText("/home/z/my-project/retro96/testdata/netscape1996.html");
        var doc = HtmlParser.Parse(html, ParsedUrl.Parse("file:///C:/web/n.htm"), new CookieStore());

        void Walk(DomNode n, int depth)
        {
            if (depth > 4) return;
            string label = n switch
            {
                DomElement e => $"<{e.TagName}{(e.Children.Count == 0 ? "/" : "")}>",
                DomText t => $"text[{(t.Data ?? "").Length}] '{(t.Data ?? "")[..Math.Min(40, (t.Data ?? "").Length)]}'",
                DomComment c => $"comment[{c.Text.Length}]",
                _ => n.GetType().Name
            };
            Console.WriteLine(new string(' ', depth * 2) + label);
            foreach (var c in n.Children.Take(12)) Walk(c, depth + 1);
            if (n.Children.Count > 12) Console.WriteLine(new string(' ', (depth + 1) * 2) + $"… {n.Children.Count - 12} more");
        }
        Walk(doc, 0);
    }
}
