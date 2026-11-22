using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;

// Markdown renderer, reused from Mote (MdReader).
static class Md
{
    static readonly Regex ListRe = new Regex(@"^( *)([-*+]|\d{1,9}[.)])( +|$)(.*)$");
    static readonly Regex TableSep = new Regex(@"^\s*\|?\s*:?-+:?\s*(\|\s*:?-+:?\s*)*\|?\s*$");

    public static List<string> CodeBlocks = new List<string>();
    static Dictionary<string, int> ids = new Dictionary<string, int>();

    public static string ToHtml(string src)
    {
        CodeBlocks.Clear(); ids.Clear();
        var lines = new List<string>(src.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'));
        for (int k = 0; k < lines.Count; k++) lines[k] = lines[k].Replace("\t", "    ");
        return Blocks(lines);
    }

    static int Indent(string s) { int n = 0; while (n < s.Length && s[n] == ' ') n++; return n; }

    static string Blocks(List<string> L)
    {
        var sb = new StringBuilder();
        var para = new List<string>();
        Action flush = delegate
        {
            if (para.Count == 0) return;
            sb.Append("<p>").Append(Inline(JoinPara(para))).Append("</p>\n");
            para.Clear();
        };
        int i = 0;
        while (i < L.Count)
        {
            string line = L[i], t = line.Trim();
            Match m;

            if (t.StartsWith("```") || t.StartsWith("~~~"))
            {
                flush();
                string fence = t.Substring(0, 3);
                string lang = t.Substring(3).Trim().Split(' ')[0];
                var code = new StringBuilder();
                for (i++; i < L.Count && !L[i].Trim().StartsWith(fence); i++) code.Append(L[i]).Append('\n');
                i++;
                string raw = code.ToString().TrimEnd('\n');
                int n = CodeBlocks.Count;
                CodeBlocks.Add(raw);
                sb.Append("<div class=\"code\"><a class=\"copy\" id=\"cp" + n + "\" href=\"sa:copy/" + n + "\">" + App.T("Copy", "Kopyala") + "</a>")
                  .Append("<pre><code>").Append(Highlight(raw, lang)).Append("</code></pre></div>\n");
                continue;
            }
            if (t.Length == 0) { flush(); i++; continue; }

            if (para.Count > 0 && Regex.IsMatch(line, @"^ {0,3}(=+|-+)\s*$"))
            {   // setext heading
                string txt = JoinPara(para); para.Clear();
                int n = t[0] == '=' ? 1 : 2;
                sb.AppendFormat("<h{0} id=\"{1}\">{2}</h{0}>\n", n, Slug(txt), Inline(txt));
                i++; continue;
            }
            if ((m = Regex.Match(line, @"^ {0,3}(#{1,6})(?:\s+(.*?))?(?:\s+#+)?\s*$")).Success)
            {
                flush();
                string txt = m.Groups[2].Value;
                sb.AppendFormat("<h{0} id=\"{1}\">{2}</h{0}>\n", m.Groups[1].Length, Slug(txt), Inline(txt));
                i++; continue;
            }
            if (Regex.IsMatch(line, @"^ {0,3}([-*_])( *\1){2,} *$"))
            {
                flush(); sb.Append("<hr>\n"); i++; continue;
            }
            if (Regex.IsMatch(line, @"^ {0,3}>"))
            {
                flush();
                var q = new List<string>();
                for (; i < L.Count && Regex.IsMatch(L[i], @"^ {0,3}>"); i++) q.Add(Regex.Replace(L[i], @"^ {0,3}> ?", ""));
                sb.Append("<blockquote>\n").Append(Blocks(q)).Append("</blockquote>\n");
                continue;
            }
            if (line.Contains("|") && i + 1 < L.Count && L[i + 1].Contains("-") && TableSep.IsMatch(L[i + 1]))
            {
                flush();
                i = Table(L, i, sb);
                continue;
            }
            if ((m = ListRe.Match(line)).Success && Indent(line) < 4)
            {
                flush();
                i = List(L, i, sb);
                continue;
            }
            para.Add(line);
            i++;
        }
        flush();
        return sb.ToString();
    }

    static string[] Cells(string row)
    {
        string r = row.Trim();
        if (r.StartsWith("|")) r = r.Substring(1);
        if (r.EndsWith("|")) r = r.Substring(0, r.Length - 1);
        string[] c = r.Split('|');
        for (int k = 0; k < c.Length; k++) c[k] = c[k].Trim();
        return c;
    }

    static int Table(List<string> L, int i, StringBuilder sb)
    {
        string[] head = Cells(L[i]), sep = Cells(L[i + 1]);
        var align = new string[sep.Length];
        for (int k = 0; k < sep.Length; k++)
        {
            bool l = sep[k].StartsWith(":"), r = sep[k].EndsWith(":");
            align[k] = l && r ? "center" : r ? "right" : l ? "left" : null;
        }
        Func<int, string> st = k => k < align.Length && align[k] != null ? " style=\"text-align:" + align[k] + "\"" : "";
        sb.Append("<table><thead><tr>");
        for (int k = 0; k < head.Length; k++) sb.Append("<th" + st(k) + ">" + Inline(head[k]) + "</th>");
        sb.Append("</tr></thead><tbody>\n");
        for (i += 2; i < L.Count && L[i].Trim().Length > 0 && L[i].Contains("|"); i++)
        {
            string[] c = Cells(L[i]);
            sb.Append("<tr>");
            for (int k = 0; k < head.Length; k++) sb.Append("<td" + st(k) + ">" + (k < c.Length ? Inline(c[k]) : "") + "</td>");
            sb.Append("</tr>\n");
        }
        sb.Append("</tbody></table>\n");
        return i;
    }

    static int List(List<string> L, int i, StringBuilder sb)
    {
        Match first = ListRe.Match(L[i]);
        int baseIndent = first.Groups[1].Length;
        bool ordered = char.IsDigit(first.Groups[2].Value[0]);
        string tag = ordered ? "ol" : "ul";
        if (ordered)
        {
            int start = int.Parse(first.Groups[2].Value.TrimEnd('.', ')'));
            sb.Append(start != 1 ? "<ol start=\"" + start + "\">\n" : "<ol>\n");
        }
        else sb.Append("<ul>\n");

        List<string> item = null;
        int contentIndent = 0;
        bool loose = false;
        Action emit = delegate
        {
            if (item == null) return;
            string cls = "", box = "";
            Match tm = Regex.Match(item[0], @"^\[([ xX])\] ");
            if (tm.Success)
            {
                cls = " class=\"task\"";
                box = "<input type=\"checkbox\" disabled" + (tm.Groups[1].Value == " " ? "" : " checked") + "> ";
                item[0] = item[0].Substring(tm.Length);
            }
            string inner = Blocks(item);
            if (!loose) inner = Regex.Replace(inner, @"^<p>([\s\S]*?)</p>\n", "$1\n");
            sb.Append("<li" + cls + ">" + box + inner.TrimEnd('\n') + "</li>\n");
        };

        bool prevBlank = false;
        while (i < L.Count)
        {
            string line = L[i];
            Match m = ListRe.Match(line);
            int ind = Indent(line);
            if (line.Trim().Length == 0)
            {
                int j = i + 1;
                while (j < L.Count && L[j].Trim().Length == 0) j++;
                if (j >= L.Count) break;
                Match nm = ListRe.Match(L[j]);
                bool sameLevelItem = nm.Success && Indent(L[j]) <= baseIndent + 1;
                if (Indent(L[j]) < contentIndent && !sameLevelItem) break;
                if (Indent(L[j]) >= contentIndent) loose = true; // blank line inside an item
                item.Add("");
                prevBlank = true; i++; continue;
            }
            if (m.Success && ind <= baseIndent + 1)
            {
                if (char.IsDigit(m.Groups[2].Value[0]) != ordered) break;
                if (prevBlank) loose = true;
                emit();
                item = new List<string>();
                item.Add(m.Groups[4].Value);
                contentIndent = m.Groups[4].Length > 0 ? m.Groups[4].Index : ind + m.Groups[2].Length + 1;
            }
            else if (ind >= contentIndent || ind > baseIndent)
                item.Add(line.Substring(Math.Min(ind, contentIndent)));
            else if (!prevBlank && ind < 4 && !Regex.IsMatch(line, @"^ {0,3}(#|>|```|~~~)"))
                item.Add(line.Trim()); // lazy continuation line
            else break;
            prevBlank = false;
            i++;
        }
        // drop trailing blank lines from the item
        while (item.Count > 1 && item[item.Count - 1].Length == 0) item.RemoveAt(item.Count - 1);
        emit();
        sb.Append("</" + tag + ">\n");
        return i;
    }

    static string JoinPara(List<string> lines)
    {
        var sb = new StringBuilder();
        for (int k = 0; k < lines.Count; k++)
        {
            string l = lines[k];
            bool br = l.EndsWith("  ") || l.EndsWith("\\");
            l = l.Trim();
            if (l.EndsWith("\\")) l = l.Substring(0, l.Length - 1);
            sb.Append(l);
            if (k < lines.Count - 1) sb.Append(br ? "\u0003\n" : "\n");
        }
        return sb.ToString();
    }

    static string Esc(string s)
    {
        return s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");
    }

    static string Slug(string s)
    {
        s = Regex.Replace(s.ToLowerInvariant(), @"[^\w\- ]", "");
        s = s.Trim().Replace(' ', '-');
        int c;
        if (ids.TryGetValue(s, out c)) { ids[s] = c + 1; s = s + "-" + c; } else ids[s] = 1;
        return Esc(s);
    }

    const string Keywords = "if|else|elif|for|foreach|while|do|return|function|def|class|struct|enum|interface|import|from|export|" +
        "const|let|var|new|true|false|null|None|True|False|nil|this|self|public|private|protected|static|void|int|string|bool|" +
        "try|catch|finally|throw|raise|except|using|namespace|package|in|of|is|as|async|await|yield|break|continue|switch|case|" +
        "default|param|lambda|with|not|and|or|fn|func|go|pub|impl|use|mut|select|where|order|by|insert|update|delete|create|table|" +
        "echo|then|fi|done|end|begin";

    static string Highlight(string code, string lang)
    {
        lang = lang.ToLowerInvariant();
        if (lang == "" || lang == "text" || lang == "txt" || lang == "md" || lang == "markdown") return Esc(code);
        string com;
        if (" py python ps ps1 powershell pwsh sh bash shell zsh yaml yml toml rb ruby r conf ini dockerfile makefile ".Contains(" " + lang + " ")) com = @"#.*";
        else if (lang == "sql" || lang == "lua") com = @"--.*";
        else if (lang == "html" || lang == "xml" || lang == "svg") com = @"<!--[\s\S]*?-->";
        else com = @"//.*|/\*[\s\S]*?\*/";
        var opt = lang == "sql" || lang.StartsWith("ps") || lang == "powershell" ? RegexOptions.IgnoreCase : RegexOptions.None;
        var re = new Regex(@"(?<c>" + com + @")|(?<s>""(?:\\.|[^""\\\n])*""|'(?:\\.|[^'\\\n])*'|`[^`\n]*`)|(?<n>\b\d+(?:\.\d+)?\b)|(?<k>\b(?:" + Keywords + @")\b)", opt);
        var sb = new StringBuilder();
        int pos = 0;
        foreach (Match m in re.Matches(code))
        {
            sb.Append(Esc(code.Substring(pos, m.Index - pos)));
            string cls = m.Groups["c"].Success ? "c" : m.Groups["s"].Success ? "s" : m.Groups["n"].Success ? "n" : "k";
            sb.Append("<span class=\"" + cls + "\">" + Esc(m.Value) + "</span>");
            pos = m.Index + m.Length;
        }
        sb.Append(Esc(code.Substring(pos)));
        return sb.ToString();
    }

    static string Inline(string s)
    {
        var codes = new List<string>();
        s = Regex.Replace(s, @"(`+)(.+?)\1", m =>
        {
            codes.Add("<code>" + Esc(m.Groups[2].Value.Trim()) + "</code>");
            return "\u0001" + (codes.Count - 1) + "\u0002";
        });
        s = Esc(s);
        s = Regex.Replace(s, @"\\([\\`*_{}\[\]()#+\-.!|~])", m => "&#" + (int)m.Groups[1].Value[0] + ";");
        s = Regex.Replace(s, @"!\[([^\]]*)\]\(([^)\s]+)(?:\s+&quot;.*?&quot;)?\)", "<img src=\"$2\" alt=\"$1\">");
        s = Regex.Replace(s, @"\[([^\]]+)\]\(([^)\s]+)(?:\s+&quot;.*?&quot;)?\)", "<a href=\"$2\">$1</a>");
        s = Regex.Replace(s, @"&lt;(https?://[^\s&]+)&gt;", "<a href=\"$1\">$1</a>");
        s = Regex.Replace(s, @"(?<![""=>;/])\b(https?://[^\s<]+[^\s<.,;:!?)'])", "<a href=\"$1\">$1</a>");
        s = Regex.Replace(s, @"\*\*(?!\s)(.+?)(?<!\s)\*\*", "<strong>$1</strong>");
        s = Regex.Replace(s, @"(?<!\w)__(?!\s)(.+?)(?<!\s)__(?!\w)", "<strong>$1</strong>");
        s = Regex.Replace(s, @"(?<![\w*])\*(?![\s*])(.+?)(?<![\s*])\*(?![\w*])", "<em>$1</em>");
        s = Regex.Replace(s, @"(?<![\w&#])_(?!\s)(.+?)(?<!\s)_(?!\w)", "<em>$1</em>");
        s = Regex.Replace(s, @"~~(.+?)~~", "<del>$1</del>");
        s = s.Replace("\u0003", "<br>");
        s = Regex.Replace(s, "\u0001(\\d+)\u0002", m => codes[int.Parse(m.Groups[1].Value)]);
        return s;
    }
}
