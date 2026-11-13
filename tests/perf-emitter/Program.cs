using System.Diagnostics;
using System.Globalization;
using System.Text;

// Writes a fixed amount of 80-column log text to stdout and records how long the write took
// next to how many bytes it pushed. The write is paced by the terminal: a pseudoconsole only
// accepts output as fast as its host drains it, so the elapsed time is how quickly the
// terminal swallows a flood. tests/perf-terminals.ps1 launches this inside each terminal.
int megabytes = args.Length > 0 ? int.Parse(args[0], CultureInfo.InvariantCulture) : 64;
string result = args.Length > 1 ? args[1] : "result.txt";

// One block of numbered lines, so no two rows are identical.
var text = new StringBuilder(1024 * 1024 + 256);
int line = 0;
while (text.Length < 1024 * 1024)
{
    text.Append(line.ToString("D8", CultureInfo.InvariantCulture))
        .Append("  2026-11-13T12:00:00.000Z  INFO  worker  completed request  path=/api/v1/items/")
        .Append((line % 100000).ToString(CultureInfo.InvariantCulture))
        .Append("  duration=").Append((line % 977).ToString(CultureInfo.InvariantCulture)).Append("ms")
        .Append("\r\n");
    line++;
}

byte[] payload = Encoding.ASCII.GetBytes(text.ToString());
Stream stdout = Console.OpenStandardOutput();

long total = 0;
var watch = Stopwatch.StartNew();
for (int i = 0; i < megabytes; i++)
{
    stdout.Write(payload, 0, payload.Length);
    total += payload.Length;
}
stdout.Flush();
watch.Stop();

File.WriteAllText(result,
    watch.Elapsed.TotalMilliseconds.ToString("F1", CultureInfo.InvariantCulture) + " " +
    total.ToString(CultureInfo.InvariantCulture));
return 0;
