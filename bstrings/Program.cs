﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using Fclp;
using NLog;
using NLog.Config;
using NLog.Targets;

namespace bstrings
{
    internal class Program
    {
        private static Logger _logger;
        private static Stopwatch _sw;
        private static readonly Dictionary<string, string> _regExPatterns = new Dictionary<string, string>();
        private static readonly Dictionary<string, string> _regExDesc = new Dictionary<string, string>();

        private static void Main(string[] args)
        {
            SetupNLog();
            SetupPatterns();

            _logger = LogManager.GetCurrentClassLogger();

            var p = new FluentCommandLineParser<ApplicationArguments>
            {
                IsCaseSensitive = false
            };

            p.Setup(arg => arg.File)
                .As('f')
                .WithDescription("File to search. This is required").Required();

            p.Setup(arg => arg.SaveTo)
                .As('o')
                .WithDescription("File to save results to");

            p.Setup(arg => arg.GetAscii)
                .As('a')
                .SetDefault(true)
                .WithDescription("If set, look for ASCII strings. Default is true. Use -a false to disable");

            p.Setup(arg => arg.GetUnicode)
                .As('u')
                .SetDefault(true)
                .WithDescription("If set, look for Unicode strings. Default is true. Use -u false to disable");

            p.Setup(arg => arg.MinimumLength)
                .As('m').SetDefault(3).WithDescription("Minimum string length. Default is 3");

            p.Setup(arg => arg.Quiet)
                .As('q').SetDefault(false).WithDescription("Quiet mode (Do not show header or total number of hits)");

            p.Setup(arg => arg.MaximumLength)
                .As('x').SetDefault(-1).WithDescription("Maximum string length. Default is unlimited");

            p.Setup(arg => arg.GetPatterns)
                .As('p').SetDefault(false).WithDescription("Display list of built in regular expressions");

            p.Setup(arg => arg.LookForString)
                .As("ls")
                .SetDefault(string.Empty)
                .WithDescription("String to look for. When set, only matching strings are returned.");

            p.Setup(arg => arg.LookForRegex)
                .As("lr")
                .SetDefault(string.Empty)
                .WithDescription("Regex to look for. When set, only matching strings are returned.");

            p.Setup(arg => arg.SortAlpha)
                .As("sa").SetDefault(false).WithDescription("Sort results alphabetically");

            p.Setup(arg => arg.SortLength)
                .As("sl").SetDefault(false).WithDescription("Sort results by length");

            //  p.Setup(arg => arg.ShowOffset).As('o').SetDefault(false).WithDescription("Show offset to hit before string");

            var header =
                $"bstrings version {Assembly.GetExecutingAssembly().GetName().Version}" +
                "\r\n\r\nAuthor: Eric Zimmerman (saericzimmerman@gmail.com)" +
                "\r\nhttps://github.com/EricZimmerman/bstrings";

            var footer = @"Examples: bstrings.exe -f ""C:\Temp\UsrClass 1.dat"" --ls URL" + "\r\n\t " +
                         @" bstrings.exe -f ""C:\Temp\someFile.txt"" --lr guid" + "\r\n\t " +
                         @" bstrings.exe -f ""C:\Temp\someOtherFile.txt"" --lr cc -sa" + "\r\n\t " +
                         @" bstrings.exe -f ""C:\Temp\someOtherFile.txt"" --lr cc -sa -m 15 -x 22" + "\r\n\t " +
                         @" bstrings.exe -f ""C:\Temp\UsrClass 1.dat"" --ls mui -sl" + "\r\n\t ";

            p.SetupHelp("?", "help").WithHeader(header).Callback(text => _logger.Info(text + "\r\n" + footer));


            var result = p.Parse(args);

            if (result.HelpCalled)
            {
                return;
            }

            if (p.Object.GetPatterns)
            {
                _logger.Info("Name \t\tDescription");
                foreach (var regExPattern in _regExPatterns)
                {
                    var desc = _regExDesc[regExPattern.Key];
                    _logger.Info($"{regExPattern.Key}\t{desc}");
                }

                _logger.Info("");
                _logger.Info("To use a built in pattern, supply the Name to the --lr switch");

                return;
            }

            if (result.HasErrors)
            {
                p.HelpOption.ShowHelp(p.Options);

                return;
            }

            if (!File.Exists(p.Object.File))
            {
                _logger.Warn($"'{p.Object.File}' not found. Exiting");
                return;
            }

            if (!p.Object.Quiet)
            {
                _logger.Info(header);
                _logger.Info("");
            }

            _sw = new Stopwatch();
            _sw.Start();

            var hits = new HashSet<string>();

            var regPattern = p.Object.LookForRegex;

            if (_regExPatterns.ContainsKey(p.Object.LookForRegex))
            {
                regPattern = _regExPatterns[p.Object.LookForRegex];
            }

            if (regPattern.Length > 0 && !p.Object.Quiet)
            {
                _logger.Info($"Searching via RegEx pattern: {regPattern}");
                _logger.Info("");
            }

            if (p.Object.SaveTo.Length > 0)
            {
                var dir = Path.GetDirectoryName(p.Object.SaveTo);

                if (dir != null && Directory.Exists(dir) == false)
                {
                    try
                    {
                        Directory.CreateDirectory(dir);
                    }
                    catch (Exception)
                    {
                        _logger.Warn($"Invalid path: '{p.Object.SaveTo}'. Results will not be saved to a file.");
                        _logger.Info("");
                        p.Object.SaveTo = string.Empty;
                    }
                }
                else
                {
                    if (dir == null)
                    {
                        _logger.Warn($"Invalid path: '{p.Object.SaveTo}");
                        p.Object.SaveTo = string.Empty;
                    }
                }

                if (p.Object.SaveTo.Length > 0 && !p.Object.Quiet)
                {
                    _logger.Info($"Saving hits to '{p.Object.SaveTo}'");
                    _logger.Info("");
                }
            }

            var minLength = 3;
            if (p.Object.MinimumLength > 0)
            {
                minLength = p.Object.MinimumLength;
            }

            var maxLength = -1;

            if (p.Object.MaximumLength > minLength)
            {
                maxLength = p.Object.MaximumLength;
            }

            var chunkSizeMb = 512;
            var chunkSizeBytes = chunkSizeMb*1024*1024;

            var fileSizeBytes = new FileInfo(p.Object.File).Length;
            var bytesRemaining = fileSizeBytes;
            var offset = 0;

            var chunkIndex = 1;
            var totalChunks = (fileSizeBytes/chunkSizeBytes) + 1;
            var hsuffix = totalChunks == 1 ? "" : "s";

            if (!p.Object.Quiet)
            {
                _logger.Info($"Searching {totalChunks:N0} chunk{hsuffix} ({chunkSizeMb} MB each)...");
                _logger.Info("");
            }

            using (var mmf = MemoryMappedFile.CreateFromFile(p.Object.File, FileMode.Open, "source"))
            {
                while (bytesRemaining > 0)
                {
                    if (bytesRemaining <= chunkSizeBytes)
                    {
                        chunkSizeBytes = (int) bytesRemaining;
                    }

                    using (var accessor = mmf.CreateViewStream(offset, chunkSizeBytes, MemoryMappedFileAccess.Read))
                    {
                        var chunk = new byte[chunkSizeBytes];

                        accessor.Read(chunk, 0, chunkSizeBytes);

                        offset += chunkSizeBytes;
                        bytesRemaining -= chunkSizeBytes;

                        if (p.Object.GetUnicode)
                        {
                            var uh = GetUnicodeHits(chunk, minLength, maxLength);
                            foreach (var h in uh)
                            {
                                hits.Add(h);
                            }
                        }

                        if (p.Object.GetAscii)
                        {
                            var ah = GetAsciiHits(chunk, minLength, maxLength);
                            foreach (var h in ah)
                            {
                                hits.Add(h);
                            }
                        }

                        if (!p.Object.Quiet)
                        {
                            _logger.Info(
                                $"Chunk {chunkIndex:N0} of {totalChunks:N0} finished. Total strings so far: {hits.Count:N0} Elapsed time: {_sw.Elapsed.TotalSeconds:N3} seconds");
                        }
                    }
                }
            }
            _sw.Stop();

            if (!p.Object.Quiet)
            {
                _logger.Info("");
            }

            if (p.Object.SortAlpha)
            {
                var tempList = hits.ToList();
                tempList.Sort();
                hits = new HashSet<string>(tempList);
            }
            else if (p.Object.SortLength)
            {
                var tempList = SortByLength(hits.ToList()).ToList();
                hits = new HashSet<string>(tempList);
            }

            var counter = 0;


            var reg = new Regex(regPattern,
                RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.IgnorePatternWhitespace);

            //set up highlighting
            var words = new HashSet<string>();
            if (p.Object.LookForString.Length > 0)
            {
                words.Add(p.Object.LookForString);
            }
            else if (p.Object.LookForRegex.Length > 0)
            {
                words.Add(regPattern);
            }

            AddHighlightingRules(words.ToList(), regPattern.Length > 0);

            StreamWriter sw = null;
            if (p.Object.SaveTo.Length > 0)
            {
                sw = new StreamWriter(p.Object.SaveTo, false);
            }

            foreach (var hit in hits)
            {
                if (hit.Length == 0)
                {
                    continue;
                }

                if (p.Object.LookForString.Length > 0 || p.Object.LookForRegex.Length > 0)
                {
                    if (p.Object.LookForString.Length > 0 &&
                        hit.IndexOf(p.Object.LookForString, StringComparison.InvariantCultureIgnoreCase) >= 0)
                    {
                        counter += 1;
                        _logger.Info(hit);
                        sw?.WriteLine(hit);
                    }
                    else if (p.Object.LookForRegex.Length > 0)
                    {
                        if (!reg.IsMatch(hit))
                        {
                            continue;
                        }
                        counter += 1;
                        _logger.Info(hit);
                        sw?.WriteLine(hit);
                    }
                }
                else
                {
                    counter += 1;
                    _logger.Info(hit);
                    sw?.WriteLine(hit);
                }
            }

            if (sw != null)
            {
                sw.Flush();
                sw.Close();
            }

            if (!p.Object.Quiet)
            {
                var suffix = counter == 1 ? "" : "s";

                _logger.Info("");
                _logger.Info($"Found {counter:N0} string{suffix} in {_sw.Elapsed.TotalSeconds:N3} seconds");
            }
        }

        private static void SetupPatterns()
        {
            _regExDesc.Add("guid", "\tFinds GUIDs");
            _regExDesc.Add("usPhone", "\tFinds US phone numbers");
            _regExDesc.Add("unc", "\tFinds UNC paths");
            _regExDesc.Add("mac", "\tFinds MAC addresses");
            _regExDesc.Add("ssn", "\tFinds US Social Security Numbers");
            _regExDesc.Add("cc", "\tFinds credit card numbers");

            _regExDesc.Add("ipv4", "\tFinds IP version 4 addresses");
            _regExDesc.Add("ipv6", "\tFinds IP version 6 addresses");
            _regExDesc.Add("email", "\tFinds email addresses");
            _regExDesc.Add("zip", "\tFinds zip codes");
            _regExDesc.Add("urlUser", "\tFinds usernames in URLs");
            _regExDesc.Add("url3986", "\tFinds URLs according to RFC 3986");
            _regExDesc.Add("xml", "\tFinds XML/HTML tags");

            _regExPatterns.Add("xml", @"\A<([A-Z][A-Z0-9]*)\b[^>]*>(.*?)</\1>\z");
            _regExPatterns.Add("guid", @"\b[A-F0-9]{8}(?:-[A-F0-9]{4}){3}-[A-F0-9]{12}\b");
            _regExPatterns.Add("usPhone", @"\(?\b[2-9][0-9]{2}\)?[-. ]?[2-9][0-9]{2}[-. ]?[0-9]{4}\b");
            _regExPatterns.Add("unc", @"^\\\\(?<server>[a-z0-9 %._-]+)\\(?<share>[a-z0-9 $%._-]+)");
            _regExPatterns.Add("mac", "\\b[0-9A-F]{2}([-:]?)(?:[0-9A-F]{2}\\1){4}[0-9A-F]{2}\\b");
            _regExPatterns.Add("ssn", "\\b(?!000)(?!666)[0-8][0-9]{2}[- ](?!00)[0-9]{2}[- ](?!0000)[0-9]{4}\\b");
            _regExPatterns.Add("cc",
                "^(?:4[0-9]{12}(?:[0-9]{3})?|5[1-5][0-9]{14}|6(?:011|5[0-9][0-9])[0-9]{12}|3[47][0-9]{13}|3(?:0[0-5]|[68][0-9])[0-9]{11}|(?:2131|1800|35\\d{3})\\d{11})$");
            _regExPatterns.Add("ipv4",
                @"\b(?:(?:25[0-5]|2[0-4][0-9]|1[0-9][0-9]|[1-9]?[0-9])\.){3}(?:25[0-5]|2[0-4][0-9]|1[0-9][0-9]|[1-9]?[0-9])\b");
            _regExPatterns.Add("ipv6", @"(?<![:.\w])(?:[A-F0-9]{1,4}:){7}[A-F0-9]{1,4}(?![:.\w])");
            _regExPatterns.Add("email", @"\A\b[A-Z0-9._%+-]+@[A-Z0-9.-]+\.[A-Z]{2,6}\b\z");
            _regExPatterns.Add("zip", @"\A\b[0-9]{5}(?:-[0-9]{4})?\b\z");
            _regExPatterns.Add("urlUser", @"^[a-z0-9+\-.]+://(?<user>[a-z0-9\-._~%!$&'()*+,;=]+)@");
            _regExPatterns.Add("url3986", @"^
		[a-z][a-z0-9+\-.]*://                       # Scheme
		([a-z0-9\-._~%!$&'()*+,;=]+@)?              # User
		(?<host>[a-z0-9\-._~%]+                     # Named host
		|\[[a-f0-9:.]+\]                            # IPv6 host
		|\[v[a-f0-9][a-z0-9\-._~%!$&'()*+,;=:]+\])  # IPvFuture host
		(:[0-9]+)?                                  # Port
		(/[a-z0-9\-._~%!$&'()*+,;=:@]+)*/?          # Path
		(\?[a-z0-9\-._~%!$&'()*+,;=:@/?]*)?         # Query
		(\#[a-z0-9\-._~%!$&'()*+,;=:@/?]*)?         # Fragment
		$");
        }

        private static void AddHighlightingRules(List<string> words, bool isRegEx = false)
        {
            var target = (ColoredConsoleTarget) LogManager.Configuration.FindTargetByName("console");
            var rule = target.WordHighlightingRules.FirstOrDefault();

            var bgColor = ConsoleOutputColor.Green;
            var fgColor = ConsoleOutputColor.Red;

            if (rule != null)
            {
                bgColor = rule.BackgroundColor;
                fgColor = rule.ForegroundColor;
            }

            target.WordHighlightingRules.Clear();

            foreach (var word in words)
            {
                var r = new ConsoleWordHighlightingRule();
                r.IgnoreCase = true;
                if (isRegEx)
                {
                    r.Regex = word;
                }
                else
                {
                    r.Text = word;
                }
                r.ForegroundColor = fgColor;
                r.BackgroundColor = bgColor;

                r.WholeWords = false;
                target.WordHighlightingRules.Add(r);
            }
        }

        private static IEnumerable<string> SortByLength(IEnumerable<string> e)
        {
            var sorted = from s in e
                orderby s.Length ascending
                select s;
            return sorted;
        }

        private static List<string> GetUnicodeHits(byte[] bytes, int minSize, int maxSize)
        {
            var maxString = maxSize == -1 ? "" : maxSize.ToString();
            var mi2 = string.Format("{0}{1}{2}{3}{4}", "{", minSize, ",", maxString, "}");

            const string uniRange = "[\u0020-\u007E]";
            var regUni = new Regex($"{uniRange}{mi2}");
            var uniString = Encoding.Unicode.GetString(bytes);

            return (from Match match in regUni.Matches(uniString) select match.Value.Trim()).ToList();
        }

        private static List<string> GetAsciiHits(byte[] bytes, int minSize, int maxSize)
        {
            var maxString = maxSize == -1 ? "" : maxSize.ToString();
            var mi2 = string.Format("{0}{1}{2}{3}{4}", "{", minSize, ",", maxString, "}");

            const string ascRange = "[\x20-\x7E]";
            var regUni = new Regex($"{ascRange}{mi2}");
            var uniString = Encoding.UTF8.GetString(bytes);

            return (from Match match in regUni.Matches(uniString) select match.Value.Trim()).ToList();
        }

        private static void SetupNLog()
        {
            var config = new LoggingConfiguration();
            var loglevel = LogLevel.Info;

            var layout = @"${message}";

            var consoleTarget = new ColoredConsoleTarget();

            config.AddTarget("console", consoleTarget);

            consoleTarget.Layout = layout;

            var rule1 = new LoggingRule("*", loglevel, consoleTarget);
            config.LoggingRules.Add(rule1);

            LogManager.Configuration = config;
        }
    }

    internal class ApplicationArguments
    {
        public string File { get; set; }
        public string SaveTo { get; set; } = string.Empty;
        public bool GetAscii { get; set; } = true;
        public bool GetUnicode { get; set; } = true;
        public string LookForString { get; set; } = string.Empty;
        public string LookForRegex { get; set; } = string.Empty;
        public int MinimumLength { get; set; } = 3;
        public int MaximumLength { get; set; } = -1;
        public bool ShowOffset { get; set; } = false;
        public bool SortLength { get; set; } = false;
        public bool SortAlpha { get; set; } = false;
        public bool Quiet { get; set; } = false;
        public bool GetPatterns { get; set; } = false;
    }
}