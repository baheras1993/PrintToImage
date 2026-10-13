using System.Net.Sockets;
using System.Text;

// SendTestPrint: sends a print job to a PrintToImage server, like an imaging device would.
//
//   SendTestPrint --title "Microsoft Word - 12345 OCT report.docx"
//   SendTestPrint --host 192.168.1.10 --title "12345" --pages 3 --pjl
//   SendTestPrint --file C:\jobs\captured.prn          (re-send a saved job file unchanged)
//   SendTestPrint --title "12345" --lpr                 (LPD/LPR on port 515 instead of raw 9100)
//   SendTestPrint --title "12345" --save test.ps        (only write the generated job to a file)

var opt = Options.Parse(args);
if (opt is null)
{
    Console.WriteLine("""
        Usage: SendTestPrint [options]
          --host <ip>          server address (default 127.0.0.1)
          --port <n>           port (default 9100, or 515 with --lpr)
          --title <text>       document name, written to %%Title (default "Test 12345")
          --pages <n>          number of pages (default 1)
          --pjl                wrap the job in a PJL header with @PJL JOB NAME
          --title-style <s>    how the title is written: plain | escaped | hex   (default plain)
          --lpr                send with the LPR protocol (job name in the control file)
          --queue <name>       LPR queue name (default "print")
          --file <path>        send an existing job file instead of generating one
          --save <path>        write the generated job to a file and do not send it
        """);
    return 2;
}

byte[] job = opt.File is not null ? File.ReadAllBytes(opt.File) : TestJob.Build(opt.Title, opt.Pages, opt.Pjl, opt.TitleStyle);

if (opt.Save is not null)
{
    File.WriteAllBytes(opt.Save, job);
    Console.WriteLine($"Wrote {job.Length} bytes to {opt.Save}");
    return 0;
}

try
{
    if (opt.Lpr) Lpr.Send(opt.Host, opt.Port ?? 515, opt.Queue, opt.Title, job);
    else
    {
        using var client = new TcpClient();
        client.Connect(opt.Host, opt.Port ?? 9100);
        using var s = client.GetStream();
        s.Write(job);
        s.Flush();
        client.Client.Shutdown(SocketShutdown.Send);
    }
    Console.WriteLine($"Sent {job.Length} bytes to {opt.Host}:{opt.Port ?? (opt.Lpr ? 515 : 9100)} ({(opt.Lpr ? "LPR" : "raw")}), title \"{opt.Title}\".");
    return 0;
}
catch (SocketException e)
{
    Console.Error.WriteLine($"Could not send to {opt.Host}: {e.Message}");
    return 1;
}

sealed class Options
{
    public string Host = "127.0.0.1";
    public int? Port;
    public string Title = "Test 12345";
    public int Pages = 1;
    public bool Pjl, Lpr;
    public string TitleStyle = "plain";
    public string Queue = "print";
    public string? File, Save;

    public static Options? Parse(string[] a)
    {
        var o = new Options();
        for (int i = 0; i < a.Length; i++)
        {
            string Next() => i + 1 < a.Length ? a[++i] : throw new ArgumentException("missing value for " + a[i]);
            switch (a[i])
            {
                case "--host": o.Host = Next(); break;
                case "--port": o.Port = int.Parse(Next()); break;
                case "--title": o.Title = Next(); break;
                case "--pages": o.Pages = Math.Clamp(int.Parse(Next()), 1, 100); break;
                case "--pjl": o.Pjl = true; break;
                case "--lpr": o.Lpr = true; break;
                case "--title-style": o.TitleStyle = Next(); break;
                case "--queue": o.Queue = Next(); break;
                case "--file": o.File = Next(); break;
                case "--save": o.Save = Next(); break;
                case "-h": case "--help": case "/?": return null;
                default: Console.Error.WriteLine("Unknown option " + a[i]); return null;
            }
        }
        return o;
    }
}

static class TestJob
{
    /// <summary>A small PostScript document similar to what a Windows PostScript driver sends.</summary>
    public static byte[] Build(string title, int pages, bool pjl, string titleStyle)
    {
        var sb = new StringBuilder();
        var utf8 = Encoding.UTF8.GetBytes(title);
        if (pjl)
        {
            sb.Append("\u001b%-12345X@PJL JOB NAME=\"").Append(Latin1(utf8).Replace("\"", "'")).Append("\"\r\n");
            sb.Append("@PJL ENTER LANGUAGE = POSTSCRIPT\r\n");
        }
        sb.Append("%!PS-Adobe-3.0\r\n");
        sb.Append("%%Title: ").Append(titleStyle switch
        {
            "hex" => "<" + Convert.ToHexString(utf8) + ">",
            "escaped" => "(" + PsEscape(utf8) + ")",
            _ => Latin1(utf8),
        }).Append("\r\n");
        sb.Append("%%Creator: SendTestPrint\r\n%%Pages: ").Append(pages).Append("\r\n%%EndComments\r\n");
        sb.Append("<< /PageSize [595 842] >> setpagedevice\r\n");
        var ascii = new string(title.Select(c => c < 128 && c >= 32 ? c : '?').ToArray());
        for (int p = 1; p <= pages; p++)
        {
            sb.Append($"%%Page: {p} {p}\r\n");
            sb.Append("0.2 0.3 0.6 setrgbcolor 40 760 515 40 rectfill\r\n");
            sb.Append("1 setgray /Helvetica-Bold findfont 20 scalefont setfont 55 773 moveto (PrintToImage test page) show\r\n");
            sb.Append("0 setgray /Helvetica findfont 12 scalefont setfont\r\n");
            sb.Append($"55 720 moveto (Document name: {PsEscape(Encoding.ASCII.GetBytes(ascii))}) show\r\n");
            sb.Append($"55 700 moveto (Page {p} of {pages}   Sent: {DateTime.Now:yyyy-MM-dd HH:mm:ss}) show\r\n");
            // A circle "scan" made of a radial gray image and some measurement lines.
            sb.Append("gsave 120 380 translate 240 240 scale 64 64 8 [64 0 0 -64 0 64] {<");
            for (int y = 0; y < 64; y++)
                for (int x = 0; x < 64; x++)
                {
                    double d = Math.Sqrt((x - 32) * (x - 32) + (y - 32) * (y - 32));
                    int v = (int)Math.Clamp(255 - d * 6 + 40 * Math.Sin(d / 2), 0, 255);
                    sb.Append(v.ToString("x2"));
                }
            sb.Append(">} image grestore\r\n");
            sb.Append("0.8 0 0 setrgbcolor 2 setlinewidth newpath 240 500 100 0 360 arc closepath stroke\r\n");
            sb.Append("0 0.5 0 setrgbcolor 1 setlinewidth [6 3] 0 setdash newpath 120 500 moveto 360 500 lineto stroke [] 0 setdash\r\n");
            sb.Append("0 setgray 400 600 moveto /Times-Roman findfont 14 scalefont setfont (Thickness: 254 um) show\r\n");
            sb.Append("400 580 moveto (Volume: 8.9 mm3) show\r\n");
            sb.Append("showpage\r\n");
        }
        sb.Append("%%EOF\r\n");
        if (pjl) sb.Append("\u001b%-12345X@PJL EOJ\r\n\u001b%-12345X");
        return Encoding.Latin1.GetBytes(sb.ToString());
    }

    private static string Latin1(byte[] b) => Encoding.Latin1.GetString(b);

    private static string PsEscape(byte[] b)
    {
        var sb = new StringBuilder();
        foreach (var c in b)
        {
            if (c is (byte)'(' or (byte)')' or (byte)'\\') sb.Append('\\').Append((char)c);
            else if (c < 32 || c >= 127) sb.Append('\\').Append(Convert.ToString(c, 8).PadLeft(3, '0'));
            else sb.Append((char)c);
        }
        return sb.ToString();
    }
}

static class Lpr
{
    public static void Send(string host, int port, string queue, string title, byte[] data)
    {
        using var client = new TcpClient();
        client.Connect(host, port);
        using var s = client.GetStream();
        void Ack(string step)
        {
            int b = s.ReadByte();
            if (b != 0) throw new IOException($"LPD server refused ({step}), reply {b}.");
        }
        var hostName = Environment.MachineName;
        int jobNo = Random.Shared.Next(1, 999);
        string dfName = $"dfA{jobNo:D3}{hostName}", cfName = $"cfA{jobNo:D3}{hostName}";
        var latin1 = Encoding.Latin1;

        s.Write(latin1.GetBytes($"\u0002{queue}\n"));
        Ack("receive job");

        var control = latin1.GetBytes($"H{hostName}\nP{Environment.UserName}\nJ{title}\nl{dfName}\nU{dfName}\nN{title}\n");
        s.Write(latin1.GetBytes($"\u0002{control.Length} {cfName}\n"));
        Ack("control file header");
        s.Write(control);
        s.WriteByte(0);
        Ack("control file");

        s.Write(latin1.GetBytes($"\u0003{data.Length} {dfName}\n"));
        Ack("data file header");
        s.Write(data);
        s.WriteByte(0);
        Ack("data file");
    }
}
