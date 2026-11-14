// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Barbatos Labs | Pham The Hung and Barbatos.SecsGem Contributors.
// All Rights Reserved.

namespace Barbatos.SecsGem;

public partial class SecsItem
{
    /// <summary>
    /// A <see cref="SecsFormat.List"/> item: an ordered, immutable sequence of child items.
    /// </summary>
    /// <remarks>
    /// The children are fixed at construction. secs4net exposes a settable indexer, which lets a
    /// caller drop an item out of the tree without disposing it (leaking its pooled buffer) and
    /// makes a shared tree unsafe to read concurrently. Here a different list means a new
    /// <see cref="ListItem"/>, which costs one array and buys both guarantees.
    /// </remarks>
    [DebuggerTypeProxy(typeof(ListItemDebugView))]
    private sealed class ListItem : SecsItem
    {
        private readonly SecsItem[] _items;

        internal ListItem(SecsItem[] items)
            : base(SecsFormat.List)
            => _items = items;

        public override int Count => _items.Length;

        public override bool IsList => true;

        public override ReadOnlySpan<SecsItem> Items => _items;

        public override SecsItem this[int index] => _items[index];

        public override void EncodeTo(IBufferWriter<byte> writer, SecsCodecOptions options)
        {
            ArgumentNullException.ThrowIfNull(writer);
            ArgumentNullException.ThrowIfNull(options);

            var items = _items.AsSpan();
            if (items.IsEmpty)
            {
                WriteEmptyItemHeader(SecsFormat.List, writer);
                return;
            }

            // A list's length field counts ITEMS, not bytes - the one place in SECS-II where it does.
            WriteItemHeader(SecsFormat.List, items.Length, writer);
            foreach (var item in items)
            {
                item.EncodeTo(writer, options);
            }
        }

        protected override void DisposeCore()
        {
            foreach (var item in _items.AsSpan())
            {
                item.Dispose();
            }
        }

        private protected override bool EqualsCore(SecsItem other)
        {
            if (other is not ListItem list || _items.Length != list._items.Length)
            {
                return false;
            }

            for (var i = 0; i < _items.Length; i++)
            {
                if (!_items[i].Equals(list._items[i]))
                {
                    return false;
                }
            }

            return true;
        }

        private sealed class ListItemDebugView(ListItem item)
        {
            [DebuggerBrowsable(DebuggerBrowsableState.RootHidden)]
            public SecsItem[] Items => item._items;
        }
    }
}
