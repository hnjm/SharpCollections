﻿// Copyright (c) Miha Zupan. All rights reserved.
// This file is licensed under the BSD-Clause 2 license. 
// See the license.txt file in the project root for more information.
using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using SharpCollections.Helpers;

namespace SharpCollections.Generic
{
    /// <summary>
    /// A compact insert-only key/value collection for fast prefix lookups
    /// <para>Something between a Trie and a full Radix tree, but stored linearly in memory</para>
    /// </summary>
    /// <typeparam name="TValue">The value associated with the key</typeparam>
    public class CompactPrefixTree<TValue>
#if !LEGACY
        : IReadOnlyDictionary<string, TValue>, IReadOnlyList<KeyValuePair<string, TValue>>
#endif
    {
        [DebuggerDisplay("{Char}, Child: {ChildChar} at {ChildIndex}, Match: {MatchIndex}, Children: {Children}")]
        private struct Node
        {
            /// <summary>
            /// The character this node represents, should never be 0
            /// </summary>
            public char Char;
            /// <summary>
            /// Will be 0 if this is a leaf node
            /// </summary>
            public char ChildChar;
            public int ChildIndex;
            /// <summary>
            /// Set to -1 if it does not point to a match
            /// </summary>
            public int MatchIndex;
            /// <summary>
            /// -1 if not present
            /// </summary>
            public int Children;
        }

        private Node[] _tree;
        private KeyValuePair<string, TValue>[] _matches;
        private int[] _children;

        private readonly int[] _asciiRootMap = new int[128];
        private Dictionary<char, int> _unicodeRootMap;

        #region Size and Capacity

        /// <summary>
        /// Gets the number of nodes in the internal tree structure
        /// <para>You might be looking for <see cref="Count"/></para>
        /// <para>Exposing this might help in deducing more efficient initial parameters</para>
        /// </summary>
        public int TreeSize { get; private set; }
        /// <summary>
        /// Gets or sets the capacity of the internal tree structure buffer
        /// <para>You might be looking for <see cref="Capacity"/></para>
        /// </summary>
        public int TreeCapacity
        {
            get
            {
                return _tree.Length;
            }
            set
            {
                if (value < TreeSize)
                    ThrowHelper.ArgumentOutOfRangeException(ExceptionArgument.value, ExceptionReason.SmallCapacity);

                if (value != TreeSize || value == 0)
                {
                    Node[] newTree = new Node[value];
                    if (TreeSize > 0)
                    {
                        Array.Copy(_tree, 0, newTree, 0, TreeSize);
                    }
                    _tree = newTree;
                }
            }
        }
        [MethodImpl(Compat.AggressiveInlining)]
        private void EnsureTreeCapacity(int min)
        {
            if (_tree.Length < min)
            {
                EnsureTreeCapacityRare(min);
            }
            Debug.Assert(_tree.Length >= min);
        }
        [MethodImpl(MethodImplOptions.NoInlining)]
        private void EnsureTreeCapacityRare(int min)
        {
            // Expansion logic as in System.Collections.Generic.List<T>
            Debug.Assert(min > _tree.Length);
            int newCapacity = _tree.Length * 2;
            if ((uint)min > int.MaxValue) newCapacity = int.MaxValue;
            if (newCapacity < min) newCapacity = min;
            TreeCapacity = newCapacity;
        }

        /// <summary>
        /// Gets the number of key/value pairs contained in the <see cref="CompactPrefixTree{TValue}"/>
        /// </summary>
        public int Count { get; private set; }
        /// <summary>
        /// Gets or sets the capacity of the internal key/value pair buffer
        /// </summary>
        public int Capacity
        {
            get
            {
                return _matches.Length;
            }
            set
            {
                if (value < Count)
                    ThrowHelper.ArgumentOutOfRangeException(ExceptionArgument.value, ExceptionReason.SmallCapacity);

                if (value != Count || value == 0)
                {
                    KeyValuePair<string, TValue>[] newMatches = new KeyValuePair<string, TValue>[value];
                    if (Count > 0)
                    {
                        Array.Copy(_matches, 0, newMatches, 0, Count);
                    }
                    _matches = newMatches;
                }
            }
        }
        [MethodImpl(Compat.AggressiveInlining)]
        private void EnsureCapacity(int min)
        {
            // Expansion logic as in System.Collections.Generic.List<T>
            if (_matches.Length < min)
            {
                EnsureCapacityRare(min);
            }
            Debug.Assert(_matches.Length >= min);
        }
        [MethodImpl(MethodImplOptions.NoInlining)]
        private void EnsureCapacityRare(int min)
        {
            // Expansion logic as in System.Collections.Generic.List<T>
            Debug.Assert(min > _matches.Length);
            int newCapacity = _matches.Length * 2;
            if ((uint)min > int.MaxValue) newCapacity = int.MaxValue;
            if (newCapacity < min) newCapacity = min;
            Capacity = newCapacity;
        }

        /// <summary>
        /// Gets the number of elements in the children buffer in the internal tree structure
        /// <para>You might be looking for <see cref="Count"/></para>
        /// <para>Exposing this might help in deducing more efficient initial parameters</para>
        /// </summary>
        public int ChildrenCount { get; private set; }
        /// <summary>
        /// Gets or sets the capacity of the internal children buffer
        /// <para>You might be looking for <see cref="Capacity"/></para>
        /// </summary>
        public int ChildrenCapacity
        {
            get
            {
                return _children.Length;
            }
            set
            {
                if (value < ChildrenCount)
                    ThrowHelper.ArgumentOutOfRangeException(ExceptionArgument.value, ExceptionReason.SmallCapacity);

                if (value != ChildrenCount || value == 0)
                {
                    int[] newChildren = new int[value];
                    if (ChildrenCount > 0)
                    {
                        Array.Copy(_children, 0, newChildren, 0, ChildrenCount);
                    }

                    // Set new odd indexes to -1
                    for (int i = ChildrenCount + 1; i < newChildren.Length; i += 2)
                        newChildren[i] = -1;

                    _children = newChildren;
                }
            }
        }
        [MethodImpl(Compat.AggressiveInlining)]
        private void EnsureChildrenCapacity(int min)
        {
            if (_children.Length < min)
            {
                EnsureChildrenCapacityRare(min);
            }
            Debug.Assert(_children.Length >= min);
        }
        [MethodImpl(MethodImplOptions.NoInlining)]
        private void EnsureChildrenCapacityRare(int min)
        {
            // Expansion logic as in System.Collections.Generic.List<T>
            Debug.Assert(min > _children.Length);
            Debug.Assert(ChildrenCount % 2 == 0);
            int newCapacity = _children.Length * 2;
            if ((uint)min > int.MaxValue) newCapacity = int.MaxValue;
            if (newCapacity < min) newCapacity = min;
            ChildrenCapacity = newCapacity;
        }

        #endregion Size and Capacity

        #region RootChar

        // Inspired by Markdig's CharacterMap

        [MethodImpl(Compat.AggressiveInlining)]
        private bool TryGetRoot(char rootChar, out int rootNodeIndex)
        {
            if (rootChar < 128)
            {
                rootNodeIndex = _asciiRootMap[rootChar];
                return rootNodeIndex != -1;
            }

            if (_unicodeRootMap != null)
            {
                return _unicodeRootMap.TryGetValue(rootChar, out rootNodeIndex);
            }

            rootNodeIndex = -1;
            return false;
        }
        private void SetRootChar(char rootChar)
        {
            if (rootChar < 128)
            {
                Debug.Assert(_asciiRootMap[rootChar] == -1);
                _asciiRootMap[rootChar] = TreeSize;
            }
            else
            {
                if (_unicodeRootMap is null)
                {
                    _unicodeRootMap = new Dictionary<char, int>();
                }
                _unicodeRootMap.Add(rootChar, TreeSize);
            }
        }

        #endregion RootChar

        /// <summary>
        /// Indicates whether matching ignores character casing.
        /// </summary>
        public readonly bool IgnoreCase;

        private void Init(int matchCapacity, int treeCapacity, int childrenCapacity)
        {
            for (int i = 0; i < _asciiRootMap.Length; i++)
                _asciiRootMap[i] = -1;

            Capacity = matchCapacity;
            TreeCapacity = treeCapacity;
            ChildrenCapacity = childrenCapacity;
        }

        /// <summary>
        /// Constructs a new <see cref="CompactPrefixTree{TValue}"/> with no initial prefixes
        /// </summary>
        public CompactPrefixTree(int matchCapacity = 0, int treeCapacity = 0, int childrenCapacity = 0, bool ignoreCase = false)
        {
            Init(matchCapacity, treeCapacity, childrenCapacity);
            IgnoreCase = ignoreCase;
        }
        /// <summary>
        /// Constructs a new <see cref="CompactPrefixTree{TValue}"/> with the supplied matches
        /// </summary>
        /// <param name="input">Matches to initialize the <see cref="CompactPrefixTree{TValue}"/> with. For best lookup performance, this collection should be sorted.</param>
        /// <param name="ignoreCase">Indicates whether matching will ignore character casing.</param>
        public CompactPrefixTree(ICollection<KeyValuePair<string, TValue>> input, bool ignoreCase = false)
        {
            if (input is null) ThrowHelper.ArgumentNullException(ExceptionArgument.input);

            Init(input.Count, input.Count * 2, input.Count * 2);
            IgnoreCase = ignoreCase;

            foreach (KeyValuePair<string, TValue> element in input)
            {
                TryInsert(in element, InsertionBehavior.ThrowOnExisting);
            }
        }

        #region this[] accessors

        /// <summary>
        /// Retrieves the key/value pair at the specified index (must be lower than <see cref="Count"/>)
        /// </summary>
        /// <param name="index">Index of pair to get, must be lower than <see cref="Count"/> (the order is the same as the order in which the elements were added)</param>
        /// <returns>The key/value pair of the element at the specified index</returns>
        public KeyValuePair<string, TValue> this[int index]
        {
            [MethodImpl(Compat.AggressiveInlining)]
            get
            {
                if ((uint)index >= (uint)Count) ThrowHelper.IndexOutOfRangeException();
                return _matches[index];
            }
        }

        /// <summary>
        /// Gets or sets the value associated with the specified key
        /// </summary>
        /// <param name="key">The key of the value to get or set</param>
        /// <returns>The value of the element with the specified key</returns>
        public TValue this[string key]
        {
            get
            {
                if (TryMatchExact(key, out KeyValuePair<string, TValue> match))
                    return match.Value;
                throw new KeyNotFoundException(key);
            }
            set
            {
                var pair = new KeyValuePair<string, TValue>(key, value);
                bool modified = TryInsert(in pair, InsertionBehavior.OverwriteExisting);
                Debug.Assert(modified);
            }
        } // Get, Set

#if NETCORE
        /// <summary>
        /// Gets the value associated with the specified key
        /// </summary>
        /// <param name="key">The key of the value to get</param>
        /// <returns>The key/value pair of the element with the specified key</returns>
        public KeyValuePair<string, TValue> this[ReadOnlySpan<char> key]
        {
            get
            {
                if (TryMatchExact(key, out KeyValuePair<string, TValue> match))
                    return match;
                throw new KeyNotFoundException(key.ToString());
            }
        } // Get only
#endif

        #endregion this[] accessors

        #region Add, TryAdd

        /// <summary>
        /// Adds the specified key/value pair to the <see cref="CompactPrefixTree{TValue}"/>
        /// </summary>
        /// <param name="key">The key of the element to add</param>
        /// <param name="value">The value of the element to add</param>
        public void Add(string key, TValue value)
        {
            var pair = new KeyValuePair<string, TValue>(key, value);
            TryInsert(in pair, InsertionBehavior.ThrowOnExisting);
        }
        /// <summary>
        /// Adds the specified key/value pair to the <see cref="CompactPrefixTree{TValue}"/>
        /// </summary>
        /// <param name="pair">The key/value pair to add</param>
        public void Add(KeyValuePair<string, TValue> pair)
            => TryInsert(in pair, InsertionBehavior.ThrowOnExisting);

        /// <summary>
        /// Tries to add the key/value pair to the <see cref="CompactPrefixTree{TValue}"/> if the key is not yet present
        /// </summary>
        /// <param name="key">The key of the element to add</param>
        /// <param name="value">The value of the element to add</param>
        /// <returns>True if the element was added, false otherwise</returns>
        public bool TryAdd(string key, TValue value)
        {
            var pair = new KeyValuePair<string, TValue>(key, value);
            return TryInsert(in pair, InsertionBehavior.None);
        }
        /// <summary>
        /// Tries to add the key/value pair to the <see cref="CompactPrefixTree{TValue}"/> if the key is not yet present
        /// </summary>
        /// <param name="pair">The pair to add</param>
        /// <returns>True if the element was added, false otherwise</returns>
        public bool TryAdd(KeyValuePair<string, TValue> pair)
            => TryInsert(in pair, InsertionBehavior.None);

        #endregion Add, TryAdd

        #region Insert internal

        private bool TryInsert(in KeyValuePair<string, TValue> pair, InsertionBehavior behavior)
        {
            string key = pair.Key;
            if (key is null) ThrowHelper.ArgumentNullException(ExceptionArgument.key);
            if (key.Length == 0) ThrowHelper.ArgumentOutOfRangeException(ExceptionArgument.key, ExceptionReason.String_Empty);
            Debug.Assert(!string.IsNullOrEmpty(key));

            char rootChar = IgnoreCase ? char.ToLowerInvariant(key[0]) : key[0];
            if (TryGetRoot(rootChar, out int rootNodeIndex))
            {
                var tree = _tree;
                ref Node node = ref tree[rootNodeIndex];
                for (int i = 1; i < key.Length; i++)
                {
                    char c = IgnoreCase ? char.ToLowerInvariant(key[i]) : key[i];

                    if (node.ChildChar == c)
                    {
                        node = ref tree[node.ChildIndex];
                        continue;
                    }

                    if (node.Children == -1)
                    {
                        // This could be a leaf node
                        if (node.ChildChar == 0)
                        {
                            // This is a leaf node
                            int previousMatchIndex = node.MatchIndex;
                            ref KeyValuePair<string, TValue> previousMatch = ref _matches[previousMatchIndex];
                            string previousKey = previousMatch.Key;

                            // Find out for how long the two keys continue to share a prefix
                            int previousIndex = i;
                            int minLength = Math.Min(key.Length, previousKey.Length);
                            if (IgnoreCase)
                            {
                                for (; i < minLength; i++)
                                    if (char.ToLowerInvariant(key[i]) != char.ToLowerInvariant(previousKey[i])) break;
                            }
                            else
                            {
                                for (; i < minLength; i++)
                                    if (key[i] != previousKey[i]) break;
                            }

                            if (i == minLength && key.Length == previousKey.Length)
                            {
                                // The two keys are of the same length and i has reached the length of the key => duplicate
                                Debug.Assert(i == key.Length && i == previousKey.Length);
                                Debug.Assert(key.Equals(previousKey, IgnoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal));
                                goto HandleDuplicateKey;
                            }

                            // Clear the match index on the current node as it's no longer a leaf node
                            node.MatchIndex = -1;

                            // insert (i - previousIndex) intermediary nodes (could be 0)
                            int intermediaryNodesToInsert = i - previousIndex;
                            if (intermediaryNodesToInsert > 0)
                            {
                                node.ChildIndex = TreeSize;
                                node.ChildChar = IgnoreCase ? char.ToLowerInvariant(key[previousIndex]) : key[previousIndex];
                                EnsureTreeCapacity(TreeSize + intermediaryNodesToInsert);
                                tree = _tree;
                                for (int j = 0; j < intermediaryNodesToInsert - 1; j++)
                                {
                                    tree[TreeSize + j] = new Node()
                                    {
                                        Char = IgnoreCase ? char.ToLowerInvariant(previousKey[previousIndex + j]) : previousKey[previousIndex + j],
                                        ChildChar = IgnoreCase ? char.ToLowerInvariant(previousKey[previousIndex + j + 1]) : previousKey[previousIndex + j + 1],
                                        ChildIndex = TreeSize + j + 1,
                                        MatchIndex = -1,
                                        Children = -1
                                    };
                                }
                                TreeSize += intermediaryNodesToInsert;
                                tree[TreeSize - 1] = new Node()
                                {
                                    Char = IgnoreCase ? char.ToLowerInvariant(previousKey[i - 1]) : previousKey[i - 1],
                                    MatchIndex = -1,
                                    Children = -1
                                };
                                node = ref tree[TreeSize - 1];
                            }

                            node.ChildIndex = TreeSize;

                            // Insert the pair
                            EnsureCapacity(Count + 1);
                            _matches[Count] = pair;

                            // One of the strings could be a prefix of the other, in that case we're only inserting one leaf node
                            if (i == minLength)
                            {
                                Debug.Assert(key.Length != previousKey.Length);
                                if (previousKey.Length < key.Length) // If the input was sorted, this should be hit
                                {
                                    Debug.Assert(key.StartsWith(previousKey, IgnoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal));
                                    c = node.ChildChar = IgnoreCase ? char.ToLowerInvariant(key[i]) : key[i];
                                    node.MatchIndex = previousMatchIndex;
                                    EnsureTreeCapacity(TreeSize + 1);
                                    _tree[TreeSize] = new Node()
                                    {
                                        Char = c,
                                        MatchIndex = Count,
                                        Children = -1
                                    };
                                }
                                else // if key.Length < previousKey.Length
                                {
                                    Debug.Assert(key.Length < previousKey.Length);
                                    Debug.Assert(previousKey.StartsWith(key, IgnoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal));
                                    c = node.ChildChar = IgnoreCase ? char.ToLowerInvariant(previousKey[i]) : previousKey[i];
                                    node.MatchIndex = Count;
                                    EnsureTreeCapacity(TreeSize + 1);
                                    _tree[TreeSize] = new Node()
                                    {
                                        Char = c,
                                        MatchIndex = previousMatchIndex,
                                        Children = -1
                                    };
                                }
                                Count++;
                                TreeSize++;
                                return true;
                            }

                            // Insert two leaf nodes
                            Debug.Assert(node.Char != 0 && node.Char == (IgnoreCase ? char.ToLowerInvariant(previousKey[i - 1]) : previousKey[i - 1]));
                            Debug.Assert(node.MatchIndex == -1);
                            Debug.Assert(node.Children == -1);

                            c = node.ChildChar = IgnoreCase ? char.ToLowerInvariant(previousKey[i]) : previousKey[i];
                            node.Children = ChildrenCount;

                            EnsureChildrenCapacity(ChildrenCount + 2);
                            _children[ChildrenCount] = TreeSize + 1;
                            ChildrenCount += 2;

                            // Insert the two leaf nodes
                            EnsureTreeCapacity(TreeSize + 2);
                            _tree[TreeSize] = new Node()
                            {
                                Char = c,
                                MatchIndex = previousMatchIndex,
                                Children = -1
                            };
                            _tree[TreeSize + 1] = new Node()
                            {
                                Char = IgnoreCase ? char.ToLowerInvariant(key[i]) : key[i],
                                MatchIndex = Count,
                                Children = -1
                            };

                            Count++;
                            TreeSize += 2;
                            return true;
                        }
                        else
                        {
                            // This node has a child char, therefore we either don't have a match attached or that match is simply a prefix of the current key
                            Debug.Assert(node.MatchIndex == -1 || key.StartsWith(_matches[node.MatchIndex].Key, IgnoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal));

                            // Set this pair as the current node's first element in the Children list
                            node.Children = ChildrenCount;
                            EnsureChildrenCapacity(ChildrenCount + 2);
                            _children[ChildrenCount] = TreeSize;
                            ChildrenCount += 2;

                            InsertLeafNode(in pair, c);
                            return true;
                        }
                    }
                    else
                    {
                        // Look for a child node with a matching Char in all of children
                        var children = _children;
                        int childrenIndex = node.Children;
                        int lastChildrenIndex = childrenIndex;
                        do
                        {
                            if ((uint)childrenIndex >= (uint)children.Length)
                                break;
                            node = ref _tree[children[childrenIndex]];
                            if (node.Char == c) goto NextChar;
                            lastChildrenIndex = childrenIndex;
                            childrenIndex = children[childrenIndex + 1];
                        }
                        while (true);

                        // A child node was not found, add a new one to children
                        EnsureChildrenCapacity(ChildrenCount + 2);
                        _children[lastChildrenIndex + 1] = ChildrenCount;
                        _children[ChildrenCount] = TreeSize;
                        ChildrenCount += 2;

                        InsertLeafNode(in pair, c);
                        return true;
                    }

                NextChar:;
                }

                // We have found our final node, check if a match already claimed this node
                if (node.MatchIndex != -1)
                {
                    ref KeyValuePair<string, TValue> previousMatch = ref _matches[node.MatchIndex];

                    // Either some other key is the leaf here, or the key is duplicated
                    if (previousMatch.Key.Length == key.Length)
                    {
                        Debug.Assert(previousMatch.Key.Equals(key, IgnoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal));
                        goto HandleDuplicateKey;
                    }
                    else
                    {
                        // It's not a duplicate but shares key.Length characters, therefore it's longer
                        // This will never occur if the input was sorted
                        Debug.Assert(previousMatch.Key.Length > key.Length);
                        Debug.Assert(previousMatch.Key.StartsWith(key, IgnoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal));
                        Debug.Assert(node.ChildChar == 0 && node.Children == -1);

                        // It is a leaf node
                        // Move the prevMatch one node inward
                        int previousMatchIndex = node.MatchIndex;
                        node.MatchIndex = Count;
                        char c = node.ChildChar = IgnoreCase ? char.ToLowerInvariant(previousMatch.Key[key.Length]) : previousMatch.Key[key.Length];
                        node.ChildIndex = TreeSize;
                        EnsureTreeCapacity(TreeSize + 1);
                        _tree[TreeSize] = new Node()
                        {
                            Char = c,
                            MatchIndex = previousMatchIndex,
                            Children = -1
                        };
                        TreeSize++;

                        // Set the pair as a match on this node
                    }
                }

                // Set the pair as a match on this node
                node.MatchIndex = Count; // This might be modifying a forgotten node reference, but in that case it was already set
                EnsureCapacity(Count + 1);
                _matches[Count] = pair;
                Count++;
                return true;

            HandleDuplicateKey:;
                Debug.Assert(key.Equals(_matches[node.MatchIndex].Key, IgnoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal));
                if (behavior == InsertionBehavior.None) return false;
                if (behavior == InsertionBehavior.OverwriteExisting)
                {
                    _matches[node.MatchIndex] = pair;
                    return true;
                }
                Debug.Assert(behavior == InsertionBehavior.ThrowOnExisting);
                ThrowHelper.ArgumentException(ExceptionArgument.key, ExceptionReason.DuplicateKey);
                Debug.Fail("Should throw by now");
                return false;
            }
            else // if the root character is not yet in the collection
            {
                SetRootChar(rootChar);
                InsertLeafNode(in pair, rootChar);
                return true;
            }
        }
        private void InsertLeafNode(in KeyValuePair<string, TValue> pair, char nodeChar)
        {
            EnsureCapacity(Count + 1);
            _matches[Count] = pair;

            EnsureTreeCapacity(TreeSize + 1);
            _tree[TreeSize] = new Node()
            {
                Char = nodeChar,
                MatchIndex = Count,
                Children = -1
            };

            Count++;
            TreeSize++;
        }

        #endregion Insert internal

        #region TryMatch longest

        /// <summary>
        /// Tries to find the longest prefix of text, starting at offset, that is contained in this <see cref="CompactPrefixTree{TValue}"/>
        /// </summary>
        /// <param name="text">The text in which to search for the prefix</param>
        /// <param name="offset">Index of the character at which to start searching</param>
        /// <param name="match">The found prefix and the corresponding value</param>
        /// <returns>True if a match was found, false otherwise</returns>
        public bool TryMatchLongest(string text, int offset, out KeyValuePair<string, TValue> match)
        {
#if NETCORE
            return TryMatchLongest(text.AsSpan(offset), out match);
#else
            return TryMatchLongest(text, offset, text.Length - offset, out match);
#endif
        }

        /// <summary>
        /// Tries to find the longest prefix of text, that is contained in this <see cref="CompactPrefixTree{TValue}"/>
        /// </summary>
        /// <param name="text">The text in which to search for the prefix</param>
        /// <param name="match">The found prefix and the corresponding value</param>
        /// <returns>True if a match was found, false otherwise</returns>
        public bool TryMatchLongest(string text, out KeyValuePair<string, TValue> match)
        {
            if (text is null) ThrowHelper.ArgumentNullException(ExceptionArgument.text);
#if NETCORE
            return TryMatchLongest(text.AsSpan(), out match);
#else
            return TryMatchLongest(text, 0, text.Length, out match);
#endif
        }

        /// <summary>
        /// Tries to find the longest prefix of text, starting at offset, that is contained in this <see cref="CompactPrefixTree{TValue}"/>
        /// </summary>
        /// <param name="text">The text in which to search for the prefix</param>
        /// <param name="offset">The offset in text at which to start looking for the prefix</param>
        /// <param name="length">The longest prefix allowed to match</param>
        /// <param name="match">The found prefix and the corresponding value</param>
        /// <returns>True if a match was found, false otherwise</returns>
#if NETCORE
        public bool TryMatchLongest(string text, int offset, int length, out KeyValuePair<string, TValue> match)
            => TryMatchLongest(text.AsSpan(offset, length), out match);

        /// <summary>
        /// Tries to find the longest prefix of text, that is contained in this <see cref="CompactPrefixTree{TValue}"/>
        /// </summary>
        /// <param name="text">The text in which to search for the prefix</param>
        /// <param name="match">The found prefix and the corresponding value</param>
        /// <returns>True if a match was found, false otherwise</returns>
        public bool TryMatchLongest(ReadOnlySpan<char> text, out KeyValuePair<string, TValue> match)
        {
            match = default;
            if (text.Length == 0 || !TryGetRoot(IgnoreCase ? char.ToLowerInvariant(text[0]) : text[0], out int nodeIndex))
                return false;
#else
        public bool TryMatchLongest(string text, int offset, int length, out KeyValuePair<string, TValue> match)
        {
            int limit = offset + length;
            if (text is null)
                ThrowHelper.ArgumentNullException(ExceptionArgument.text);

            if (offset < 0 || length < 0 || text.Length < limit)
                ThrowHelper.ArgumentOutOfRangeException(ExceptionArgument.offsetLength, ExceptionReason.InvalidOffsetLength);

            match = default;
            if (length == 0 || !TryGetRoot(IgnoreCase ? char.ToLowerInvariant(text[offset]) : text[offset], out int nodeIndex))
                return false;
#endif

            int matchIndex = -1;
            int depth = 1;

            ref Node node = ref _tree[nodeIndex];
            if (node.ChildChar == 0) goto LeafNodeFound;
            if (node.MatchIndex != -1) matchIndex = node.MatchIndex;

#if NETCORE
            for (int i = 1; i < text.Length; i++)
#else
            for (int i = offset + 1; i < limit; i++)
#endif
            {
                char c = IgnoreCase ? char.ToLowerInvariant(text[i]) : text[i];

                if (node.ChildChar == c)
                {
                    node = ref _tree[node.ChildIndex];
                    goto NextChar;
                }

                var children = _children;
                int childrenIndex = node.Children;
                do
                {
                    if ((uint)childrenIndex >= (uint)children.Length)
                        goto Return;
                    node = ref _tree[children[childrenIndex]];
                    if (node.Char == c) goto NextChar;
                    childrenIndex = children[childrenIndex + 1];
                }
                while (true);

            NextChar:;
                depth++;
                if (node.ChildChar == 0) goto LeafNodeFound;
                if (node.MatchIndex != -1) matchIndex = node.MatchIndex;
            }
            // We have ran out of our string, return the longest match we've found
            goto Return;

        LeafNodeFound:;
            ref KeyValuePair<string, TValue> possibleMatch = ref _matches[node.MatchIndex];
            var possibleKey = possibleMatch.Key;
#if NETCORE
            if (possibleKey.Length <= text.Length)
            {
                // Check that the rest of the strings match
                int sliceLength = possibleKey.Length - depth;
                if (text.Slice(depth, sliceLength).Equals(possibleKey.AsSpan(depth, sliceLength), IgnoreCase))
                {
                    matchIndex = node.MatchIndex;
                }
            }
#else
            if (possibleKey.Length <= length)
            {
                // Check that the rest of the strings match
                if (IgnoreCase)
                {
                    for (int i = offset + depth, j = depth; j < possibleKey.Length; i++, j++)
                    {
                        if (char.ToLowerInvariant(text[i]) != char.ToLowerInvariant(possibleKey[j]))
                            goto Return;
                    }
                }
                else
                {
                    for (int i = offset + depth, j = depth; j < possibleKey.Length; i++, j++)
                    {
                        if (text[i] != possibleKey[j])
                            goto Return;
                    }
                }
                matchIndex = node.MatchIndex;
            }
#endif

        Return:;
            if (matchIndex != -1)
            {
                match = _matches[matchIndex];
                return true;
            }
            return false;
        }

        #endregion TryMatch longest

        #region TryMatch exact

        /// <summary>
        /// Tries to find a suffix of text, starting at offset, that is contained in this <see cref="CompactPrefixTree{TValue}"/> and is exactly (text.Length - offset) characters long
        /// </summary>
        /// <param name="text">The text in which to search for the prefix</param>
        /// <param name="offset">Index of the character at which to start searching</param>
        /// <param name="match">The found prefix and the corresponding value</param>
        /// <returns>True if a match was found, false otherwise</returns>
        public bool TryMatchExact(string text, int offset, out KeyValuePair<string, TValue> match)
        {
#if NETCORE
            return TryMatchExact(text.AsSpan(offset), out match);
#else
            return TryMatchExact(text, offset, text.Length - offset, out match);
#endif
        }

        /// <summary>
        /// Tries to find a prefix of text, that is contained in this <see cref="CompactPrefixTree{TValue}"/> and is exactly text.Length characters long
        /// </summary>
        /// <param name="text">The text in which to search for the prefix</param>
        /// <param name="match">The found prefix and the corresponding value</param>
        /// <returns>True if a match was found, false otherwise</returns>
        public bool TryMatchExact(string text, out KeyValuePair<string, TValue> match)
        {
            if (text is null) ThrowHelper.ArgumentNullException(ExceptionArgument.text);
#if NETCORE
            return TryMatchExact(text.AsSpan(), out match);
#else
            return TryMatchExact(text, 0, text.Length, out match);
#endif
        }

        /// <summary>
        /// Tries to find a prefix of text, starting at offset, that is contained in this <see cref="CompactPrefixTree{TValue}"/> and is exactly length characters long
        /// </summary>
        /// <param name="text">The text in which to search for the prefix</param>
        /// <param name="offset">The offset in text at which to start looking for the prefix</param>
        /// <param name="length">The longest prefix allowed to match</param>
        /// <param name="match">The found prefix and the corresponding value</param>
        /// <returns>True if a match was found, false otherwise</returns>
#if NETCORE
        public bool TryMatchExact(string text, int offset, int length, out KeyValuePair<string, TValue> match)
            => TryMatchExact(text.AsSpan(offset, length), out match);

        /// <summary>
        /// Tries to find a prefix of text, that is contained in this <see cref="CompactPrefixTree{TValue}"/> and is exactly text.Length characters long
        /// </summary>
        /// <param name="text">The text in which to search for the prefix</param>
        /// <param name="match">The found prefix and the corresponding value</param>
        /// <returns>True if a match was found, false otherwise</returns>
        public bool TryMatchExact(ReadOnlySpan<char> text, out KeyValuePair<string, TValue> match)
        {
            match = default;
            if (text.Length == 0 || !TryGetRoot(IgnoreCase ? char.ToLowerInvariant(text[0]) : text[0], out int nodeIndex))
                return false;
#else
        public bool TryMatchExact(string text, int offset, int length, out KeyValuePair<string, TValue> match)
        {
            int limit = offset + length;
            if (text is null)
                ThrowHelper.ArgumentNullException(ExceptionArgument.text);

            if (offset < 0 || length < 0 || text.Length < limit)
                ThrowHelper.ArgumentOutOfRangeException(ExceptionArgument.offsetLength, ExceptionReason.InvalidOffsetLength);

            match = default;
            if (length == 0 || !TryGetRoot(IgnoreCase ? char.ToLowerInvariant(text[offset]) : text[offset], out int nodeIndex))
                return false;
#endif
            int depth = 1;

            ref Node node = ref _tree[nodeIndex];
            if (node.ChildChar == 0) goto LeafNodeFound;
#if NETCORE
            if (node.MatchIndex != -1 && text.Length == 1)
#else
            if (node.MatchIndex != -1 && length == 1)
#endif
            {
                match = _matches[node.MatchIndex];
                return true;
            }

#if NETCORE
            for (int i = 1; i < text.Length; i++)
#else
            for (int i = offset + 1; i < limit; i++)
#endif
            {
                char c = IgnoreCase ? char.ToLowerInvariant(text[i]) : text[i];

                if (node.ChildChar == c)
                {
                    node = ref _tree[node.ChildIndex];
                    goto NextChar;
                }

                var children = _children;
                int childrenIndex = node.Children;
                do
                {
                    if ((uint)childrenIndex >= (uint)children.Length)
                        return false;
                    node = ref _tree[children[childrenIndex]];
                    if (node.Char == c) goto NextChar;
                    childrenIndex = children[childrenIndex + 1];
                }
                while (true);

            NextChar:;
                depth++;
                if (node.ChildChar == 0) goto LeafNodeFound;
            }

            if (node.MatchIndex == -1) return false;
            match = _matches[node.MatchIndex];
#if NETCORE
            Debug.Assert(match.Key.Length == text.Length);
#else
            Debug.Assert(match.Key.Length == length);
#endif
            return true;

        LeafNodeFound:;
            match = _matches[node.MatchIndex];
            var key = match.Key;
#if NETCORE
            return key.Length == text.Length &&
                text.Slice(depth).Equals(key.AsSpan(depth), IgnoreCase);
#else
            if (key.Length == length)
            {
                // Check that the rest of the strings match
                if (IgnoreCase)
                {
                    for (int i = offset + depth, j = depth; j < key.Length; i++, j++)
                    {
                        if (char.ToLowerInvariant(text[i]) != char.ToLowerInvariant(key[j]))
                            return false;
                    }
                }
                else
                {
                    for (int i = offset + depth, j = depth; j < key.Length; i++, j++)
                    {
                        if (text[i] != key[j])
                            return false;
                    }
                }
                return true;
            }
            return false;
#endif
        }

        #endregion TryMatch exact

        #region TryMatch shortest

        /// <summary>
        /// Tries to find the shortest prefix of text, starting at offset, that is contained in this <see cref="CompactPrefixTree{TValue}"/>
        /// </summary>
        /// <param name="text">The text in which to search for the prefix</param>
        /// <param name="offset">Index of the character at which to start searching</param>
        /// <param name="match">The found prefix and the corresponding value</param>
        /// <returns>True if a match was found, false otherwise</returns>
        public bool TryMatchShortest(string text, int offset, out KeyValuePair<string, TValue> match)
        {
#if NETCORE
            return TryMatchShortest(text.AsSpan(offset), out match);
#else
            return TryMatchShortest(text, offset, text.Length - offset, out match);
#endif
        }

        /// <summary>
        /// Tries to find the shortest prefix of text, that is contained in this <see cref="CompactPrefixTree{TValue}"/>
        /// </summary>
        /// <param name="text">The text in which to search for the prefix</param>
        /// <param name="match">The found prefix and the corresponding value</param>
        /// <returns>True if a match was found, false otherwise</returns>
        public bool TryMatchShortest(string text, out KeyValuePair<string, TValue> match)
        {
            if (text is null) ThrowHelper.ArgumentNullException(ExceptionArgument.text);
#if NETCORE
            return TryMatchShortest(text.AsSpan(), out match);
#else
            return TryMatchShortest(text, 0, text.Length, out match);
#endif
        }

        /// <summary>
        /// Tries to find the shortest prefix of text, starting at offset, that is contained in this <see cref="CompactPrefixTree{TValue}"/>
        /// </summary>
        /// <param name="text">The text in which to search for the prefix</param>
        /// <param name="offset">The offset in text at which to start looking for the prefix</param>
        /// <param name="length">The longest prefix allowed to match</param>
        /// <param name="match">The found prefix and the corresponding value</param>
        /// <returns>True if a match was found, false otherwise</returns>
#if NETCORE
        public bool TryMatchShortest(string text, int offset, int length, out KeyValuePair<string, TValue> match)
            => TryMatchShortest(text.AsSpan(offset, length), out match);

        /// <summary>
        /// Tries to find the shortest prefix of text, that is contained in this <see cref="CompactPrefixTree{TValue}"/>
        /// </summary>
        /// <param name="text">The text in which to search for the prefix</param>
        /// <param name="match">The found prefix and the corresponding value</param>
        /// <returns>True if a match was found, false otherwise</returns>
        public bool TryMatchShortest(ReadOnlySpan<char> text, out KeyValuePair<string, TValue> match)
        {
            match = default;
            if (text.Length == 0 || !TryGetRoot(IgnoreCase ? char.ToLowerInvariant(text[0]) : text[0], out int nodeIndex))
                return false;
#else
        public bool TryMatchShortest(string text, int offset, int length, out KeyValuePair<string, TValue> match)
        {
            int limit = offset + length;
            if (text is null)
                ThrowHelper.ArgumentNullException(ExceptionArgument.text);

            if (offset < 0 || length < 0 || text.Length < limit)
                ThrowHelper.ArgumentOutOfRangeException(ExceptionArgument.offsetLength, ExceptionReason.InvalidOffsetLength);

            match = default;
            if (length == 0 || !TryGetRoot(IgnoreCase ? char.ToLowerInvariant(text[offset]) : text[offset], out int nodeIndex))
                return false;
#endif
            ref Node node = ref _tree[nodeIndex];
            if (node.MatchIndex != -1)
            {
                match = _matches[node.MatchIndex];
                return true;
            }

#if NETCORE
            for (int i = 1; i < text.Length; i++)
#else
            for (int i = offset + 1; i < limit; i++)
#endif
            {
                char c = IgnoreCase ? char.ToLowerInvariant(text[i]) : text[i];

                if (node.ChildChar == c)
                {
                    node = ref _tree[node.ChildIndex];
                    goto NextChar;
                }

                var children = _children;
                int childrenIndex = node.Children;
                do
                {
                    if ((uint)childrenIndex >= (uint)children.Length)
                        return false;
                    node = ref _tree[children[childrenIndex]];
                    if (node.Char == c) goto NextChar;
                    childrenIndex = children[childrenIndex + 1];
                }
                while (true);

            NextChar:;
                if (node.MatchIndex != -1)
                {
                    match = _matches[node.MatchIndex];
                    return true;
                }
            }
            Debug.Assert(node.MatchIndex == -1);
            return false;
        }

        #endregion TryMatch shortest

        #region Interface implementations

        /// <summary>
        /// Determines whether the <see cref="CompactPrefixTree{TValue}"/> contains the specified key
        /// </summary>
        /// <param name="key">The key to locate in this <see cref="CompactPrefixTree{TValue}"/></param>
        /// <returns>True if the key is contained in this PrefixTree, false otherwise.</returns>
        public bool ContainsKey(string key)
            => TryMatchExact(key, out _);

        /// <summary>
        /// Gets the value associated with the specified key
        /// </summary>
        /// <param name="key">The key of the value to get</param>
        /// <param name="value">The value associated with the specified key</param>
        /// <returns>True if the key is contained in this PrefixTree, false otherwise.</returns>
        public bool TryGetValue(string key, out TValue value)
        {
            bool ret = TryMatchExact(key, out KeyValuePair<string, TValue> match);
            value = match.Value;
            return ret;
        }

        /// <summary>
        /// Gets a collection containing the keys in this <see cref="CompactPrefixTree{TValue}"/>
        /// </summary>
        public IEnumerable<string> Keys
        {
            get
            {
                for (int i = 0; i < Count; i++)
                    yield return _matches[i].Key;
            }
        }
        /// <summary>
        /// Gets a collection containing the values in this <see cref="CompactPrefixTree{TValue}"/>
        /// </summary>
        public IEnumerable<TValue> Values
        {
            get
            {
                for (int i = 0; i < Count; i++)
                    yield return _matches[i].Value;
            }
        }

        /// <summary>
        /// Returns an Enumerator that iterates through the <see cref="CompactPrefixTree{TValue}"/>.
        /// <para>Use the index accessor instead (<see cref="this[int]"/>)</para>
        /// </summary>
        /// <returns></returns>
        public IEnumerator<KeyValuePair<string, TValue>> GetEnumerator() => new Enumerator(_matches);
#if !LEGACY
        IEnumerator IEnumerable.GetEnumerator() => new Enumerator(_matches);
#endif

        /// <summary>
        /// Enumerates the elements of a <see cref="CompactPrefixTree{TValue}"/>
        /// </summary>
        public struct Enumerator : IEnumerator<KeyValuePair<string, TValue>>, IEnumerator
        {
            private readonly KeyValuePair<string, TValue>[] _matches;
            private int _index;

            internal Enumerator(KeyValuePair<string, TValue>[] matches)
            {
                _matches = matches;
                _index = -1;
            }

            /// <summary>
            /// Increments the internal index
            /// </summary>
            /// <returns>True if the index is less than the length of the internal array</returns>
            public bool MoveNext() => ++_index < _matches.Length;
            /// <summary>
            /// Gets the <see cref="KeyValuePair{TKey, TValue}"/> at the current position
            /// </summary>
            public KeyValuePair<string, TValue> Current => _matches[_index];
            object IEnumerator.Current => _matches[_index];

            /// <summary>
            /// Does nothing
            /// </summary>
            public void Dispose() { }

            /// <summary>
            /// Resets the internal index to the beginning of the array
            /// </summary>
            public void Reset()
            {
                _index = -1;
            }
        }

        #endregion Interface implementations
    }
}
