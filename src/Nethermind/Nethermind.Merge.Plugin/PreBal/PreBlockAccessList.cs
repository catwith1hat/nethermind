// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Int256;
using Nethermind.Serialization.FluentRlp;

namespace Nethermind.Merge.Plugin.PreBal;

/// <summary>
/// Pre-execution storage slot entry (<c>[key, value]</c>) in a <see cref="PreAccountState"/>.
/// </summary>
public readonly record struct PreStorageSlot(UInt256 Key, UInt256 Value);

/// <summary>
/// Pre-execution <c>BLOCKHASH</c> entry (<c>[block_number, block_hash]</c>) in a <see cref="PreBlockAccessList"/>.
/// </summary>
public readonly record struct PreBlockHash(ulong BlockNumber, Hash256 BlockHash);

/// <summary>
/// Pre-execution state of an accessed account in a <see cref="PreBlockAccessList"/>.
/// </summary>
public sealed class PreAccountState
{
    private readonly Dictionary<UInt256, UInt256> _storageMap;

    /// <summary>
    /// Initializes a new <see cref="PreAccountState"/>.
    /// </summary>
    public PreAccountState(
        Address address,
        bool exists,
        ulong nonce,
        UInt256 balance,
        byte[] code,
        PreStorageSlot[] storage)
    {
        Address = address;
        Exists = exists;
        Nonce = nonce;
        Balance = balance;
        Code = code;
        Storage = storage;

        _storageMap = [with(storage.Length, UInt256Comparer.GetOptimized())];
        for (int i = 0; i < storage.Length; i++)
        {
            _storageMap.TryAdd(storage[i].Key, storage[i].Value);
        }
    }

    /// <summary>Gets the 20-byte account address.</summary>
    public Address Address { get; }

    /// <summary>Gets whether the account is non-empty prior to block execution.</summary>
    public bool Exists { get; }

    /// <summary>Gets the pre-block account nonce.</summary>
    public ulong Nonce { get; }

    /// <summary>Gets the pre-block account balance.</summary>
    public UInt256 Balance { get; }

    /// <summary>Gets the pre-block contract bytecode (empty array when none).</summary>
    public byte[] Code { get; }

    /// <summary>Gets the strictly ascending pre-block storage slots.</summary>
    public PreStorageSlot[] Storage { get; }

    /// <summary>Checks whether <paramref name="key"/> is declared in this account's storage list.</summary>
    public bool ContainsSlot(in UInt256 key) => _storageMap.ContainsKey(key);

    /// <summary>Looks up the pre-block value of <paramref name="key"/>.</summary>
    public bool TryGetSlot(in UInt256 key, out UInt256 value) => _storageMap.TryGetValue(key, out value);
}

/// <summary>
/// Canonical decoded Pre-Execution Block Access List (<c>PreBlockAccessList</c>).
/// </summary>
public sealed class PreBlockAccessList
{
    private readonly Dictionary<AddressAsKey, PreAccountState> _accountMap;
    private readonly Dictionary<ulong, Hash256> _blockHashMap;

    /// <summary>
    /// Initializes a new <see cref="PreBlockAccessList"/>.
    /// </summary>
    public PreBlockAccessList(
        byte[] parentHeader,
        PreAccountState[] accounts,
        PreBlockHash[] blockHashes)
    {
        ParentHeader = parentHeader;
        Accounts = accounts;
        BlockHashes = blockHashes;

        _accountMap = [with(accounts.Length)];
        for (int i = 0; i < accounts.Length; i++)
        {
            _accountMap.TryAdd(accounts[i].Address, accounts[i]);
        }

        _blockHashMap = [with(blockHashes.Length)];
        for (int i = 0; i < blockHashes.Length; i++)
        {
            _blockHashMap.TryAdd(blockHashes[i].BlockNumber, blockHashes[i].BlockHash);
        }
    }

    /// <summary>Gets the raw RLP bytes of the parent <see cref="BlockHeader"/>.</summary>
    public byte[] ParentHeader { get; }

    /// <summary>Gets the strictly ascending accessed accounts.</summary>
    public PreAccountState[] Accounts { get; }

    /// <summary>Gets the strictly ascending accessed ancestor block hashes.</summary>
    public PreBlockHash[] BlockHashes { get; }

    /// <summary>Checks whether <paramref name="address"/> is declared in <see cref="Accounts"/>.</summary>
    public bool ContainsAccount(Address address) => _accountMap.ContainsKey(address);

    /// <summary>Looks up the pre-execution state for <paramref name="address"/>.</summary>
    public bool TryGetAccount(Address address, [NotNullWhen(true)] out PreAccountState? account) =>
        _accountMap.TryGetValue(address, out account);

    /// <summary>Checks whether <paramref name="slot"/> on <paramref name="address"/> is declared.</summary>
    public bool ContainsSlot(Address address, in UInt256 slot) =>
        _accountMap.TryGetValue(address, out PreAccountState? account) && account.ContainsSlot(in slot);

    /// <summary>Looks up the pre-execution hash of <paramref name="blockNumber"/>.</summary>
    public bool TryGetBlockHash(ulong blockNumber, [NotNullWhen(true)] out Hash256? blockHash) =>
        _blockHashMap.TryGetValue(blockNumber, out blockHash);
}

/// <summary>
/// Exception thrown when EVM or system execution accesses an account or storage slot missing from <c>pre_bal</c>.
/// </summary>
public sealed class MissingPreBalStateException(string message) : Exception(message);

/// <summary>
/// Exception thrown when the <c>BLOCKHASH</c> opcode queries an in-range block number missing from <c>pre_bal</c>.
/// </summary>
public sealed class MissingPreBalBlockHashException(ulong blockNumber)
    : Exception($"Missing pre_bal block hash for block number {blockNumber}.")
{
    /// <summary>Gets the missing block number.</summary>
    public ulong BlockNumber { get; } = blockNumber;
}

/// <summary>
/// Canonical RLP encoder and strict decoder for <see cref="PreBlockAccessList"/>.
/// </summary>
public static class PreBlockAccessListDecoder
{
    private const byte RlpFalse = 0x80;
    private const byte RlpTrue = 0x01;

    /// <summary>
    /// Decodes a canonical RLP <see cref="PreBlockAccessList"/>, enforcing all wire-format, sorting, uniqueness,
    /// and EIP-161 existence invariants.
    /// </summary>
    /// <exception cref="RlpReaderException">Thrown when any canonical RLP or structural invariant is violated.</exception>
    public static PreBlockAccessList Decode(ReadOnlySpan<byte> data)
    {
        if (data.IsEmpty)
        {
            throw new RlpReaderException("Empty pre_bal RLP payload.");
        }

        RlpReader outer = new(data);
        RlpReader root = outer.EnterSequence(out int rootItemLength);

        byte[] parentHeader = root.ReadBytes().ToArray();
        if (parentHeader.Length == 0)
        {
            throw new RlpReaderException("PreBlockAccessList parent_header must not be empty.");
        }

        PreAccountState[] accounts = DecodeAccounts(ref root);
        PreBlockHash[] blockHashes = DecodeBlockHashes(ref root);

        outer.ExitSequence(in root, rootItemLength);
        if (outer.HasNext)
        {
            throw new RlpReaderException("Trailing bytes after PreBlockAccessList RLP sequence.");
        }

        return new PreBlockAccessList(parentHeader, accounts, blockHashes);
    }

    /// <summary>
    /// Encodes <paramref name="preBal"/> into canonical RLP bytes.
    /// </summary>
    public static byte[] Encode(PreBlockAccessList preBal)
    {
        ArgumentNullException.ThrowIfNull(preBal);
        return RlpCodec.Write(preBal, static (ref RlpWriter writer, PreBlockAccessList value) =>
        {
            writer.WriteSequence(value, static (ref RlpWriter rootWriter, PreBlockAccessList item) =>
            {
                rootWriter.Write(item.ParentHeader);

                rootWriter.WriteSequence(item.Accounts, static (ref RlpWriter accountsWriter, PreAccountState[] accounts) =>
                {
                    for (int i = 0; i < accounts.Length; i++)
                    {
                        accountsWriter.WriteSequence(accounts[i], static (ref RlpWriter accWriter, PreAccountState acc) =>
                        {
                            accWriter.Write(acc.Address.Bytes);
                            if (acc.Exists)
                            {
                                accWriter.Write(1UL);
                            }
                            else
                            {
                                accWriter.Write(ReadOnlySpan<byte>.Empty);
                            }
                            accWriter.Write(acc.Nonce);
                            WriteUInt256(ref accWriter, acc.Balance);
                            accWriter.Write(acc.Code);

                            accWriter.WriteSequence(acc.Storage, static (ref RlpWriter storageWriter, PreStorageSlot[] slots) =>
                            {
                                for (int j = 0; j < slots.Length; j++)
                                {
                                    storageWriter.WriteSequence(slots[j], static (ref RlpWriter slotWriter, PreStorageSlot slot) =>
                                    {
                                        WriteUInt256(ref slotWriter, slot.Key);
                                        WriteUInt256(ref slotWriter, slot.Value);
                                    });
                                }
                            });
                        });
                    }
                });

                rootWriter.WriteSequence(item.BlockHashes, static (ref RlpWriter hashesWriter, PreBlockHash[] hashes) =>
                {
                    for (int i = 0; i < hashes.Length; i++)
                    {
                        hashesWriter.WriteSequence(hashes[i], static (ref RlpWriter bhWriter, PreBlockHash bh) =>
                        {
                            bhWriter.Write(bh.BlockNumber);
                            bhWriter.Write(bh.BlockHash.Bytes);
                        });
                    }
                });
            });
        });
    }

    private static PreAccountState[] DecodeAccounts(ref RlpReader root)
    {
        RlpReader accountsReader = root.EnterSequence(out int accountsItemLength);
        int count = accountsReader.CountItems();
        PreAccountState[] accounts = count == 0 ? [] : new PreAccountState[count];

        Address? previousAddress = null;
        for (int i = 0; i < count; i++)
        {
            RlpReader accReader = accountsReader.EnterSequence(out int accItemLength);

            ReadOnlySpan<byte> addressBytes = accReader.ReadBytes(Address.Size);
            if (previousAddress is not null && previousAddress.Bytes.SequenceCompareTo(addressBytes) >= 0)
            {
                throw new RlpReaderException("PreBlockAccessList accounts must be strictly sorted ascending by address without duplicates.");
            }
            Address address = new(addressBytes);
            previousAddress = address;

            bool exists = ReadCanonicalBool(ref accReader);
            ulong nonce = accReader.ReadInteger<ulong>();
            UInt256 balance = ReadUInt256(ref accReader);
            byte[] code = accReader.ReadBytes().ToArray();

            if (!exists)
            {
                if (nonce != 0 || !balance.IsZero || code.Length != 0)
                {
                    throw new RlpReaderException($"PreAccountState for non-existent account {address} must have zero nonce, zero balance, and empty code.");
                }
            }
            else if (nonce == 0 && balance.IsZero && code.Length == 0)
            {
                throw new RlpReaderException($"PreAccountState for existing account {address} violates EIP-161 non-empty invariant.");
            }

            PreStorageSlot[] storage = DecodeStorageSlots(ref accReader, address);

            accountsReader.ExitSequence(in accReader, accItemLength);
            accounts[i] = new PreAccountState(address, exists, nonce, balance, code, storage);
        }

        root.ExitSequence(in accountsReader, accountsItemLength);
        return accounts;
    }

    private static PreStorageSlot[] DecodeStorageSlots(ref RlpReader accReader, Address address)
    {
        RlpReader storageReader = accReader.EnterSequence(out int storageItemLength);
        int count = storageReader.CountItems();
        PreStorageSlot[] slots = count == 0 ? [] : new PreStorageSlot[count];

        bool hasPrevious = false;
        UInt256 previousKey = default;
        for (int i = 0; i < count; i++)
        {
            RlpReader slotReader = storageReader.EnterSequence(out int slotItemLength);
            UInt256 key = ReadUInt256(ref slotReader);
            if (hasPrevious && key <= previousKey)
            {
                throw new RlpReaderException($"PreAccountState storage slots for {address} must be strictly sorted ascending by key without duplicates.");
            }
            hasPrevious = true;
            previousKey = key;

            UInt256 value = ReadUInt256(ref slotReader);
            storageReader.ExitSequence(in slotReader, slotItemLength);
            slots[i] = new PreStorageSlot(key, value);
        }

        accReader.ExitSequence(in storageReader, storageItemLength);
        return slots;
    }

    private static PreBlockHash[] DecodeBlockHashes(ref RlpReader root)
    {
        RlpReader hashesReader = root.EnterSequence(out int hashesItemLength);
        int count = hashesReader.CountItems();
        PreBlockHash[] hashes = count == 0 ? [] : new PreBlockHash[count];

        bool hasPrevious = false;
        ulong previousNumber = 0;
        for (int i = 0; i < count; i++)
        {
            RlpReader bhReader = hashesReader.EnterSequence(out int bhItemLength);
            ulong blockNumber = bhReader.ReadInteger<ulong>();
            if (hasPrevious && blockNumber <= previousNumber)
            {
                throw new RlpReaderException("PreBlockAccessList block_hashes must be strictly sorted ascending by block_number without duplicates.");
            }
            hasPrevious = true;
            previousNumber = blockNumber;

            Hash256 blockHash = new(bhReader.ReadBytes(Keccak.Size));
            hashesReader.ExitSequence(in bhReader, bhItemLength);
            hashes[i] = new PreBlockHash(blockNumber, blockHash);
        }

        root.ExitSequence(in hashesReader, hashesItemLength);
        return hashes;
    }

    private static bool ReadCanonicalBool(ref RlpReader reader)
    {
        ReadOnlySpan<byte> encoded = reader.ReadEncodedItem();
        if (encoded.Length == 1)
        {
            if (encoded[0] == RlpFalse) return false;
            if (encoded[0] == RlpTrue) return true;
        }

        throw new RlpReaderException("Invalid RLP boolean in PreAccountState.exists: expected 0x80 (false) or 0x01 (true).");
    }

    private static UInt256 ReadUInt256(ref RlpReader reader)
    {
        ReadOnlySpan<byte> bytes = reader.ReadIntegerBytes();
        if (bytes.Length > 32)
        {
            throw new RlpReaderException($"UInt256 RLP scalar length {bytes.Length} exceeds 32 bytes.");
        }

        return new UInt256(bytes, isBigEndian: true);
    }

    private static void WriteUInt256(ref RlpWriter writer, in UInt256 value)
    {
        if (value.IsZero)
        {
            writer.Write(ReadOnlySpan<byte>.Empty);
            return;
        }

        Span<byte> bigEndian = stackalloc byte[32];
        value.ToBigEndian(bigEndian);
        writer.WriteIntegerBytes(bigEndian);
    }
}
