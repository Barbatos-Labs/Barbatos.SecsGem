// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Barbatos Labs | Pham The Hung and Barbatos.SecsGem Contributors.
// All Rights Reserved.

namespace Barbatos.SecsGem;

public partial class SecsItem
{
    /// <summary>
    /// An <see cref="SecsFormat.ASCII"/> or <see cref="SecsFormat.JIS8"/> item.
    /// </summary>
    /// <remarks>
    /// Text is held as a <see cref="string"/> rather than as bytes, because that is what callers
    /// want and because the decoder can then intern short strings - SECS traffic repeats model
    /// numbers, recipe ids and alarm text endlessly.
    /// <see cref="Count"/> is the character count; for ASCII that equals the byte count on the
    /// wire, for JIS-8 under a multi-byte encoding it may not.
    /// </remarks>
    [DebuggerTypeProxy(typeof(StringItemDebugView))]
    private sealed class StringItem : SecsItem
    {
        private readonly string _value;

        internal StringItem(SecsFormat format, string value)
            : base(format)
            => _value = value;

        public override int Count => _value.Length;

        public override string GetString() => _value;

        public override void EncodeTo(IBufferWriter<byte> writer, SecsCodecOptions options)
        {
            ArgumentNullException.ThrowIfNull(writer);
            ArgumentNullException.ThrowIfNull(options);

            if (_value.Length == 0)
            {
                WriteEmptyItemHeader(Format, writer);
                return;
            }

            var encoding = options.GetEncoding(Format);
            var byteLength = encoding.GetByteCount(_value);

            WriteItemHeader(Format, byteLength, writer);
            var written = encoding.GetBytes(_value, writer.GetSpan(byteLength));
            Debug.Assert(written == byteLength, "Encoding.GetByteCount disagreed with Encoding.GetBytes.");
            writer.Advance(written);
        }

        private protected override bool EqualsCore(SecsItem other)
            => Format == other.Format
            && other is StringItem text
            && string.Equals(_value, text._value, StringComparison.Ordinal);

        private sealed class StringItemDebugView(StringItem item)
        {
            public string Value => item._value;
        }
    }
}
