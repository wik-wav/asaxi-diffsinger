using System;
using System.Collections.Generic;
using System.Linq;
using OpenUtau.Api;
using OpenUtau.Core.DiffSinger;

namespace Asaxi {
    /// <summary>
    /// DiffSinger phonemizer for Asaxi.
    ///
    /// <para>The attribute's <c>language</c> is <c>"ASA: Asaxi"</c>, not the bare code <c>"ASA"</c>.
    /// OpenUtau groups the phonemizer menu by that string verbatim, and its heading is the key
    /// itself when the theme has no <c>languages.&lt;key&gt;</c> translation — which it does not for
    /// Asaxi. The existing Asaxi CVVC plugin declares this same display string, so matching it is
    /// what makes both Asaxi phonemizers share one "ASA: Asaxi" submenu instead of getting a bare
    /// "ASA" heading each. It does not affect phoneme output: the <c>asa/</c> prefix comes from
    /// <see cref="GetLangCode"/>, which stays <c>asa</c>.</para>
    ///
    /// It is a <see cref="DiffSingerG2pPhonemizer"/>, so the host still reads the singer's
    /// <c>dsdict-asa.yaml</c> for the SYMBOL TABLE (names, types, which phones the bank can
    /// actually sing) and merges it with a rule G2P that supplies the WORDS. That split is the
    /// point: the dictionary never needs one entry per word, only one entry per phone.
    ///
    /// <para><b>Prefixed output.</b> <c>GetLangCode()</c> returning <c>"asa"</c> is what makes
    /// the host rewrite every bare phone this G2P emits into <c>asa/&lt;phone&gt;</c>, exactly as
    /// <c>[DIFFS EN]</c> emits <c>en/*</c> for a multi-language bank. A bank that lacks
    /// <c>asa/kx</c> falls back to the bare name for that one phone rather than failing the
    /// whole word.</para>
    /// </summary>
    [Phonemizer("DiffSinger Asaxi Phonemizer", "DIFFS ASA", "wik_wav", "ASA: Asaxi")]
    public class DiffSingerAsaxiPhonemizer : DiffSingerG2pPhonemizer {
        /// <summary>
        /// The singer-side dictionary this phonemizer asks for. It carries the <c>symbols:</c>
        /// block (type of every <c>asa/*</c> phone) and may carry no <c>entries:</c> at all,
        /// because words come from the rules.
        /// </summary>
        protected override string GetDictionaryName() => "dsdict-asa.yaml";

        /// <summary>Language code; the bank's language id and the <c>asa/</c> prefix both use it.</summary>
        public override string GetLangCode() => "asa";

        protected override IG2p LoadBaseG2p() => new AsaxiG2p();

        protected override string[] GetBaseG2pVowels() => AsaxiG2p.Vowels.Concat(AsaxiG2p.Nuclei).ToArray();

        protected override string[] GetBaseG2pConsonants() => AsaxiG2p.Consonants;

        /// <summary>
        /// Wraps the loaded G2P so that a phonetic hint may name any phone the singer can actually
        /// sing, not only the ones the rule G2P produces.
        ///
        /// <para>Why: the host validates a hint symbol with
        /// <c>g2p.IsValidSymbol(s) &amp;&amp; the bank has s</c>, and the rule G2P only claims its
        /// own <c>asa/*</c> tokens. A UST written against the concatenative Asaxi phonemizer puts
        /// that bank's vocabulary in the hints — <c>[y a w]</c>, <c>[a y]</c> — so every bare name
        /// failed the first half of that test, the symbol was dropped, the note had nothing left to
        /// sing and the bank errored. The voicebank had the phones all along; the phonemizer was
        /// refusing to look at them.</para>
        ///
        /// <para>Word queries are untouched: <see cref="IG2p.Query"/> passes straight through, so
        /// lyrics still phonemise to <c>asa/*</c>. Only hint validation widens, which is also how a
        /// bare-symbol plugin such as the Polish one already behaves.</para>
        /// </summary>
        protected override IG2p LoadG2p(string rootPath, bool useLangId = false) {
            return new SingerVocabularyG2p(base.LoadG2p(rootPath, useLangId), IsPhonemeSupported);
        }

        /// <summary>Delegates everything; widens only <c>IsValidSymbol</c> to the singer's table.</summary>
        sealed class SingerVocabularyG2p : IG2p {
            readonly IG2p inner;
            readonly Func<string, bool> singerHas;

            public SingerVocabularyG2p(IG2p inner, Func<string, bool> singerHas) {
                this.inner = inner;
                this.singerHas = singerHas;
            }

            public bool IsValidSymbol(string symbol) =>
                inner.IsValidSymbol(symbol)
                || (!string.IsNullOrEmpty(symbol) && singerHas(symbol));

            public bool IsVowel(string symbol) => inner.IsVowel(symbol);

            public bool IsGlide(string symbol) => inner.IsGlide(symbol);

            /// <summary>
            /// A cluster the bank cannot sing is split into its base phone plus a glide —
            /// <c>kw</c> to <c>k</c> + <c>w</c>, <c>ky</c> to <c>k</c> + <c>j</c> — so a bank
            /// without the coarticulated phone still sings the word instead of losing it. A bank
            /// that HAS the cluster is untouched: the token passes through as it always did.
            /// </summary>
            public string[] Query(string grapheme) {
                var phonemes = inner.Query(grapheme);
                if (phonemes == null) {
                    return null;
                }
                var outPhonemes = new List<string>(phonemes.Length);
                foreach (var symbol in phonemes) {
                    if (AsaxiG2p.TryDegrade(symbol, singerHas, out var degrades)) {
                        outPhonemes.AddRange(degrades);
                    } else {
                        outPhonemes.Add(symbol);
                    }
                }
                return outPhonemes.ToArray();
            }

            public string[] UnpackHint(string hint, char separator = ' ') =>
                inner.UnpackHint(hint, separator);
        }
    }
}
