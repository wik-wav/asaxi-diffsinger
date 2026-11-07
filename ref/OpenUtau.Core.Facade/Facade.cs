// Reference assembly for the Asaxi DiffSinger phonemizer.
//
// This is NOT OpenUtau. It declares exactly the surface the plugin binds to, with the same
// names, signatures and virtual flags as OpenUtau.Core, so that
//
//   * the plugin compiles without any OpenUtau installed, and so never needs rebuilding just
//     because OpenUtau shipped a new build, and
//   * at run time the CLR resolves `OpenUtau.Core` to the host's real assembly, because the
//     assembly name here is the same.
//
// Keep this file in step with `tests/Conformance`, which reflects over the REAL
// OpenUtau.Core.dll and asserts that every member below still exists, is still virtual,
// and that each override in the plugin still reaches a base definition above itself.
// If OpenUtau ever changes one of these signatures, that check fails and the plugin needs a
// real fix — this file cannot paper over an API change.

using System;

namespace OpenUtau.Api {
    [AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
    public class PhonemizerAttribute : Attribute {
        public string Name { get; private set; }
        public string Tag { get; private set; }
        public string Author { get; private set; }
        public string Language { get; private set; }

        public PhonemizerAttribute(string name, string tag, string author = null,
                                   string language = null) {
            Name = name;
            Tag = tag;
            Author = author;
            Language = language;
        }
    }

    public interface IG2p {
        bool IsValidSymbol(string symbol);
        bool IsVowel(string symbol);
        bool IsGlide(string symbol);
        string[] Query(string grapheme);
        string[] UnpackHint(string hint, char separator = ' ');
    }

    public abstract class Phonemizer {
        public string Name { get; set; }
        public string Tag { get; set; }
        public string Language { get; set; }

        /// <summary>Stub of the host's Note. Field NAMES are what the plugin binds to.</summary>
        public struct Note {
            public string lyric;
            public string phoneticHint;
            public int tone;
            public int position;
            public int duration;
        }

        /// <summary>Stub of the host's Result; the plugin only passes it through.</summary>
        public struct Result {
        }

        public abstract Result Process(Note[] notes, Note? prev, Note? next,
                                       Note? prevNeighbour, Note? nextNeighbour, Note[] prevs);

        /// <summary>
        /// Runs before Process and is where the phonemes are computed. The plugin overrides it to
        /// rewrite phonetic hints; the project and track types are stubs and never touched.
        /// </summary>
        public virtual void SetUp(Note[][] groups, OpenUtau.Core.Ustx.UProject project,
                                  OpenUtau.Core.Ustx.UTrack track) { }
    }
}

namespace OpenUtau.Core.DiffSinger {
    public abstract class DiffSingerG2pPhonemizer : OpenUtau.Api.Phonemizer {
        protected virtual string GetDictionaryName() => "dsdict.yaml";

        public virtual string GetLangCode() => string.Empty;

        protected virtual OpenUtau.Api.IG2p LoadBaseG2p() => null;

        protected virtual string[] GetBaseG2pVowels() => new string[] { };

        protected virtual string[] GetBaseG2pConsonants() => new string[] { };

        // The real DiffSingerG2pPhonemizer overrides this from DiffSingerBasePhonemizer (where it
        // is `protected virtual IG2p LoadG2p(string rootPath, bool useLangId = false)`), so the
        // stub must expose it as virtual too or the plugin cannot override it.
        protected virtual OpenUtau.Api.IG2p LoadG2p(string rootPath, bool useLangId = false) => null;

        // Declared on DiffSingerBasePhonemizer in the real assembly, which the stub collapses into
        // this one type. Not overridden by the plugin - only called.
        protected bool IsPhonemeSupported(string symbol) => false;

        // Concrete here so a subclass can call base.Process(...); the host's own implementation
        // is what actually runs.
        public override Result Process(Note[] notes, Note? prev, Note? next,
                                       Note? prevNeighbour, Note? nextNeighbour, Note[] prevs) =>
            default;
    }
}

namespace OpenUtau.Core.Ustx {
    /// <summary>Stubs: the plugin's SetUp override only needs the parameter types.</summary>
    public class UProject { }
    public class UTrack { }
}
