using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Asaxi;
using OpenUtau.Api;

namespace Conformance {
    /// <summary>
    /// Three things, in order of what they protect:
    ///
    /// 1. **Conformance.** The C# rules reproduce the Python reference on 355 words.
    /// 2. **Host binding.** The plugin was compiled against the reference assembly in
    ///    <c>ref/</c>, not against an installed OpenUtau, so this is what proves the built DLL
    ///    actually binds to the REAL <c>OpenUtau.Core</c> at run time. Every override must
    ///    land on a real base member (<c>GetBaseDefinition</c> reaches past our type).
    ///    Run this after every OpenUtau update: if it fails, an API changed and the plugin
    ///    needs a real fix — a rebuild alone will not help.
    /// 3. **Discovery.** Exactly one public <c>[Phonemizer]</c> type, instantiable.
    ///
    ///   dotnet run --project tests/Conformance -c Release
    /// </summary>
    static class Program {
        static int failures;

        static void Check(string what, bool ok, string detail = null) {
            Console.WriteLine("  {0,-58} {1}{2}", what, ok ? "ok" : "FAIL",
                detail == null ? "" : "  " + detail);
            if (!ok) {
                failures++;
            }
        }

        static int Main(string[] args) {
            var here = Path.GetDirectoryName(typeof(Program).Assembly.Location);
            var tsv = Path.Combine(here, "asa_conformance.tsv");
            if (!File.Exists(tsv)) {
                tsv = Path.GetFullPath(Path.Combine(here, "..", "..", "..", "asa_conformance.tsv"));
            }
            if (!File.Exists(tsv)) {
                Console.Error.WriteLine("conformance table not found: " + tsv);
                return 2;
            }

            // ---------------------------------------------------------------- conformance
            var g2p = new AsaxiG2p();
            int total = 0, bad = 0;
            var shown = new List<string>();
            foreach (var line in File.ReadAllLines(tsv).Skip(1)) {
                if (string.IsNullOrWhiteSpace(line)) {
                    continue;
                }
                var parts = line.Split('\t');
                if (parts.Length < 2) {
                    continue;
                }
                total++;
                var want = parts[1].Split(' ', StringSplitOptions.RemoveEmptyEntries);
                var got = g2p.Query(parts[0]) ?? Array.Empty<string>();
                if (!want.SequenceEqual(got)) {
                    bad++;
                    if (shown.Count < 20) {
                        shown.Add(string.Format("    {0,-14} want {1,-30} got {2}",
                            parts[0], string.Join(" ", want), string.Join(" ", got)));
                    }
                }
            }
            Console.WriteLine("conformance: {0}/{1} words", total - bad, total);
            foreach (var s in shown) {
                Console.WriteLine(s);
            }
            if (bad > 0) {
                failures++;
            }

            // ---------------------------------------------------------------- host binding
            Console.WriteLine();
            Console.WriteLine("host binding (real OpenUtau.Core {0}):",
                typeof(Phonemizer).Assembly.GetName().Version);
            var asm = typeof(Phonemizer).Assembly;

            var baseType = typeof(OpenUtau.Core.DiffSinger.DiffSingerG2pPhonemizer);
            Check("DiffSingerG2pPhonemizer is public", baseType.IsPublic);
            Check("DiffSingerG2pPhonemizer : Phonemizer", typeof(Phonemizer).IsAssignableFrom(baseType));
            Check("IG2p is public", typeof(IG2p).IsPublic);
            foreach (var m in typeof(IG2p).GetMethods()) {
                Check("IG2p." + m.Name + "(" + string.Join(",", m.GetParameters().Select(p => p.ParameterType.Name)) + ")",
                    m.DeclaringType == typeof(IG2p));
            }
            Check("PhonemizerAttribute takes (name, tag, author?, language?)",
                typeof(PhonemizerAttribute).GetConstructors().Any(c => {
                    var ps = c.GetParameters();
                    return ps.Length == 4 && ps[0].ParameterType == typeof(string)
                        && ps[3].ParameterType == typeof(string)
                        && ps[3].IsOptional;
                }));

            // every override must reach a base definition above our own type
            var mine = typeof(DiffSingerAsaxiPhonemizer);
            var expected = new[] {
                "GetLangCode", "GetDictionaryName", "LoadBaseG2p",
                "GetBaseG2pVowels", "GetBaseG2pConsonants", "LoadG2p",
            };
            foreach (var name in expected) {
                var method = mine.GetMethods(BindingFlags.Instance | BindingFlags.Public
                    | BindingFlags.NonPublic)
                    .FirstOrDefault(m => m.Name == name && m.DeclaringType == mine);
                var isOverride = method != null && method.GetBaseDefinition() != method;
                Check(name + " overrides a real base member", isOverride);
            }

            // the facade also declares IsPhonemeSupported (non-virtual, called not overridden)
            var isSupported = baseType.GetMethod("IsPhonemeSupported",
                BindingFlags.Instance | BindingFlags.NonPublic);
            Check("IsPhonemeSupported exists on the base",
                isSupported != null && isSupported.ReturnType == typeof(bool)
                && isSupported.GetParameters().Length == 1);

            // asm is the host assembly only because the plugin bound to it; if the plugin had
            // resolved its own copy, LoadBaseG2p would still work but this identity would differ
            Check("plugin bound to the host assembly instance",
                ReferenceEquals(typeof(DiffSingerAsaxiPhonemizer).BaseType.Assembly, asm));

            // ---------------------------------------------------------------- discovery
            Console.WriteLine();
            var pluginAsm = typeof(AsaxiG2p).Assembly;
            var marked = pluginAsm.GetTypes()
                .Where(t => t.IsPublic && !t.IsAbstract)
                .Select(t => (type: t, attr: t.GetCustomAttribute<PhonemizerAttribute>()))
                .Where(x => x.attr != null)
                .ToList();
            Check("exactly one [Phonemizer] type", marked.Count == 1);
            foreach (var (type, attr) in marked) {
                Console.WriteLine("    {0}  tag={1}  lang={2}", attr.Name, attr.Tag, attr.Language);
                var instance = (Phonemizer)Activator.CreateInstance(type);
                Check("instantiable", instance != null);
                Check("language code is asa", ((DiffSingerAsaxiPhonemizer)instance).GetLangCode() == "asa",
                    ((DiffSingerAsaxiPhonemizer)instance).GetLangCode());
                // the menu groups by this verbatim; "ASA" would give the plugin its own heading
                Check("menu language groups with the Asaxi plugin", attr.Language == "ASA: Asaxi",
                    attr.Language);
            }

            // ---------------------------------------------------------------- inventory
            var emitted = File.ReadAllLines(tsv).Skip(1)
                .Where(l => l.Contains('\t'))
                .SelectMany(l => l.Split('\t')[1].Split(' ', StringSplitOptions.RemoveEmptyEntries))
                .Distinct().ToArray();
            var undeclared = emitted.Where(s => !g2p.IsValidSymbol(s)).ToArray();
            Console.WriteLine();
            Check("every emitted symbol is declared", undeclared.Length == 0,
                undeclared.Length == 0 ? null : string.Join(" ", undeclared));
            Check("inventory is 104", emitted.Length == 104, emitted.Length.ToString());

            // ---------------------------------------------------------------- cluster splitting
            Console.WriteLine();
            string[] rep;
            Check("kw splits to k + w",
                AsaxiG2p.TrySplitCluster("kw", out var s1, out var g1) && s1 == "k" && g1 == "w");
            Check("ky splits to k + j",
                AsaxiG2p.TrySplitCluster("ky", out var s2, out var g2) && s2 == "k" && g2 == "j");
            Check("nyw splits to ny + w",
                AsaxiG2p.TrySplitCluster("nyw", out var s3, out var g3) && s3 == "ny" && g3 == "w");
            Check("ny does NOT split (it is /ɲ/)", !AsaxiG2p.TrySplitCluster("ny", out _, out _));
            Check("hy does NOT split (it is /ç/)", !AsaxiG2p.TrySplitCluster("hy", out _, out _));

            // a bank WITH the cluster: passes through untouched
            var rich = new HashSet<string> { "asa/ky", "asa/k", "asa/j" };
            Check("bank that has asa/ky keeps it",
                !AsaxiG2p.TryDegrade("asa/ky", s => rich.Contains(s), null, out rep));

            // a bank WITHOUT it but with both halves: degrades
            var poor = new HashSet<string> { "asa/k", "asa/j" };
            Check("bank without asa/ky degrades to asa/k + asa/j",
                AsaxiG2p.TryDegrade("asa/ky", s => poor.Contains(s), null, out rep)
                && rep.Length == 2 && rep[0] == "asa/k" && rep[1] == "asa/j",
                rep == null ? "(no split)" : string.Join(" ", rep));

            // aspiration: stop + release, ALWAYS, because no bank of this family has a
            // trained aspirated series. The release is the hh token, whose placeholder is en/hh.
            Check("kx splits to k + hh (aspiration, not a glide)",
                AsaxiG2p.TrySplitCluster("kx", out var s4, out var g4) && s4 == "k" && g4 == "hh");
            Check("MustSplit: aspiration always, palatalisation only as fallback",
                AsaxiG2p.MustSplit("kx") && AsaxiG2p.MustSplit("px") && !AsaxiG2p.MustSplit("ky"));
            var withAspiration = new HashSet<string> { "asa/kx", "asa/k", "asa/hh" };
            Check("aspiration splits even when the bank has asa/kx",
                AsaxiG2p.TryDegrade("asa/kx", s => withAspiration.Contains(s), null, out rep)
                && rep.Length == 2 && rep[0] == "asa/k" && rep[1] == "asa/hh",
                rep == null ? "(no split)" : string.Join(" ", rep));

            // a placeholder alias: the bank HAS asa/bw, but it is the same id as asa/b, so it
            // carries none of the labialisation and must still be split
            var aliased = new HashSet<string> { "asa/bw", "asa/b", "asa/w" };
            Check("placeholder alias (asa/bw == asa/b) still splits",
                AsaxiG2p.TryDegrade("asa/bw", s => aliased.Contains(s),
                                    (c, b) => c == "asa/bw" && b == "asa/b", out rep)
                && rep.Length == 2 && rep[0] == "asa/b" && rep[1] == "asa/w",
                rep == null ? "(no split)" : string.Join(" ", rep));

            // a REAL labialised phone (its own id) must be kept
            var genuine = new HashSet<string> { "asa/gw", "asa/g", "asa/w" };
            Check("a genuine asa/gw is kept, not split",
                !AsaxiG2p.TryDegrade("asa/gw", s => genuine.Contains(s), (c, b) => false, out rep));

            // a bank with neither half: leave it alone, the host reports it as before
            Check("bank without the glide does not degrade",
                !AsaxiG2p.TryDegrade("asa/ky", s => s == "asa/k", null, out rep));

            // ---------------------------------------------------------------- pause phones
            Console.WriteLine();
            Check("AP phonemises to AP", g2p.Query("AP")?.SequenceEqual(new[] { "AP" }) == true,
                string.Join(" ", g2p.Query("AP") ?? new string[0]));
            Check("SP phonemises to SP", g2p.Query("SP")?.SequenceEqual(new[] { "SP" }) == true,
                string.Join(" ", g2p.Query("SP") ?? new string[0]));
            Check("lower-case ap/sp also resolve", 
                g2p.Query("ap")?.SequenceEqual(new[] { "AP" }) == true
                && g2p.Query("sp")?.SequenceEqual(new[] { "SP" }) == true);
            Check("a word containing ap/sp is untouched",
                string.Join(" ", g2p.Query("appa")) == "a cl p a"
                && string.Join(" ", g2p.Query("spa")) == "s p a",
                string.Join(" ", g2p.Query("spa")));
            Check("AP/SP are valid and vowel-placed",
                g2p.IsValidSymbol("AP") && g2p.IsVowel("AP")
                && g2p.IsValidSymbol("SP") && g2p.IsVowel("SP"));
            Check("AP/SP pass through a phonetic hint",
                g2p.UnpackHint("asa/kx AP SP")?.SequenceEqual(new[] { "AP", "SP" }) == true,
                string.Join(" ", g2p.UnpackHint("asa/kx AP SP") ?? new string[0]));
            Check("AP/SP are not in the 104-token inventory",
                !emitted.Contains("AP") && !emitted.Contains("SP"));

            Console.WriteLine();
            Console.WriteLine("RESULT: " + (failures == 0 ? "pass" : "FAIL (" + failures + ")"));
            return failures == 0 ? 0 : 1;
        }
    }
}
