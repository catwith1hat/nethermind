// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Blockchain;
using Nethermind.Core;
using Nethermind.Core.BlockAccessLists;
using Nethermind.Core.Crypto;
using Nethermind.Core.Eip2930;
using Nethermind.Core.Specs;
using Nethermind.Evm;
using Nethermind.Evm.State;
using Nethermind.Evm.Tracing.State;
using Nethermind.Int256;

namespace Nethermind.Merge.Plugin.PreBal;

/// <summary>
/// Closed-world, trie-less <see cref="IWorldStateScopeProvider"/> backed strictly by a decoded <see cref="PreBlockAccessList"/>.
/// </summary>
internal sealed class PreBalScopeProvider(PreBlockAccessList preBal, Hash256 parentStateRoot) : IWorldStateScopeProvider
{
    public bool HasRoot(BlockHeader? baseBlock) => baseBlock?.StateRoot == parentStateRoot;

    public bool TryBeginScopeAtTarget(BlockHeader targetBlock, LocalMetrics metrics, [NotNullWhen(true)] out IWorldStateScopeProvider.IScope? scope)
    {
        scope = new Scope(preBal, parentStateRoot);
        return true;
    }

    public bool HasStateForTargetBlock(BlockHeader targetBlock) => true;

    public bool TryBeginScope(BlockHeader? baseBlock, LocalMetrics metrics, [NotNullWhen(true)] out IWorldStateScopeProvider.IScope? scope)
    {
        scope = new Scope(preBal, parentStateRoot);
        return true;
    }

    private sealed class Scope(PreBlockAccessList preBal, Hash256 rootHash) : IWorldStateScopeProvider.IScope
    {
        private readonly CodeDb _codeDb = new(preBal);
        private readonly Dictionary<AddressAsKey, StorageTree> _storageTrees = [];

        public Hash256 RootHash { get; } = rootHash;

        public bool StorageRootsAreAuthoritative => false;

        public IWorldStateScopeProvider.ICodeDb CodeDb => _codeDb;

        public void UpdateRootHash() { }

        public Account? Get(Address address)
        {
            if (!preBal.TryGetAccount(address, out PreAccountState? preAcc))
            {
                throw new MissingPreBalStateException($"Account {address} is missing from pre_bal.");
            }

            if (!preAcc.Exists)
            {
                return null;
            }

            ValueHash256 codeHash = preAcc.Code.Length == 0
                ? Keccak.OfAnEmptyString.ValueHash256
                : ValueKeccak.Compute(preAcc.Code);

            return new Account(preAcc.Nonce, preAcc.Balance, Keccak.EmptyTreeHash, new Hash256(codeHash));
        }

        public void HintGet(Address address, Account? account) { }

        public IWorldStateScopeProvider.IStorageTree CreateStorageTree(Address address)
        {
            if (!_storageTrees.TryGetValue(address, out StorageTree? tree))
            {
                tree = new StorageTree(preBal, address);
                _storageTrees.Add(address, tree);
            }

            return tree;
        }

        public IWorldStateScopeProvider.IWorldStateWriteBatch StartWriteBatch(int estimatedAccountNum) =>
            NoOpWriteBatch.Instance;

        public void Commit(ulong blockNumber) { }

        public Task HintBal(ReadOnlyBlockAccessList bal, IWorldStateScopeProvider.IAsyncBalReaderSink? sink = null) =>
            Task.CompletedTask;

        public void ApplyBal(ReadOnlyBlockAccessList bal, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("ApplyBal is not used in sequential Pre-BAL consumer execution.");

        public void Dispose() { }
    }

    private sealed class CodeDb : IWorldStateScopeProvider.ICodeDb, IWorldStateScopeProvider.ICodeSetter
    {
        private readonly Dictionary<ValueHash256, byte[]> _codes = [];

        public CodeDb(PreBlockAccessList preBal)
        {
            _codes[Keccak.OfAnEmptyString.ValueHash256] = Array.Empty<byte>();
            for (int i = 0; i < preBal.Accounts.Length; i++)
            {
                byte[] code = preBal.Accounts[i].Code;
                if (code.Length > 0)
                {
                    _codes[ValueKeccak.Compute(code)] = code;
                }
            }
        }

        public ReadOnlyMemory<byte> GetCode(in ValueHash256 codeHash)
        {
            if (_codes.TryGetValue(codeHash, out byte[]? code))
            {
                return code;
            }

            throw new MissingPreBalStateException($"Contract code for hash {codeHash} is missing from pre_bal.");
        }

        public IWorldStateScopeProvider.ICodeSetter BeginCodeWrite() => this;

        public bool ContainsCode(in ValueHash256 codeHash) => _codes.ContainsKey(codeHash);

        public void Set(in ValueHash256 codeHash, ReadOnlySpan<byte> code) =>
            _codes[codeHash] = code.Length == 0 ? Array.Empty<byte>() : code.ToArray();

        public void Dispose() { }
    }

    private sealed class StorageTree(PreBlockAccessList preBal, Address address) : IWorldStateScopeProvider.IStorageTree
    {
        public Hash256 RootHash => Keccak.EmptyTreeHash;

        public void Get(in UInt256 index, out UInt256 value)
        {
            if (!preBal.TryGetAccount(address, out PreAccountState? account))
            {
                throw new MissingPreBalStateException($"Account {address} is missing from pre_bal when reading slot {index}.");
            }

            if (!account.TryGetSlot(in index, out value))
            {
                throw new MissingPreBalStateException($"Storage slot {index} for account {address} is missing from pre_bal.");
            }
        }

        public void HintSet(in UInt256 index) { }
    }

    private sealed class NoOpWriteBatch : IWorldStateScopeProvider.IWorldStateWriteBatch, IWorldStateScopeProvider.IStorageWriteBatch
    {
        public static readonly NoOpWriteBatch Instance = new();

        public event EventHandler<IWorldStateScopeProvider.AccountUpdated>? OnAccountUpdated
        {
            add { }
            remove { }
        }

        public bool AcceptsStorageWrites => false;

        public void Set(Address key, Account? account) { }

        public IWorldStateScopeProvider.IStorageWriteBatch CreateStorageWriteBatch(Address key, int estimatedEntries) => this;

        public void Set(in UInt256 index, in UInt256 value) { }

        public void Clear() { }

        public void Dispose() { }
    }
}

/// <summary>
/// Outer Closed-World <see cref="IWorldState"/> decorator that intercepts every account and storage operation before
/// <see cref="TracedAccessWorldState"/> or <c>WorldState</c> caches or defaults any value, skips MPT root hashing,
/// and suppresses EIP-2930 warm-ups from querying the backing Fake Store.
/// </summary>
internal sealed class PreBalWorldState(IWorldState inner, PreBlockAccessList preBal, Hash256 expectedStateRoot)
    : WorldStateDecorator(inner), IWorldState, IReadOnlyStateProvider
{
    private static readonly UInt256 s_zeroBalance = UInt256.Zero;
    private static readonly ValueHash256 s_emptyCodeHash = Keccak.OfAnEmptyString.ValueHash256;

    private readonly PreBlockAccessList _preBal = preBal;
    private readonly Hash256 _expectedStateRoot = expectedStateRoot;
    private int _systemAccountReadSuppressionDepth;
    private int _systemCallDepth;

    public new Hash256 StateRoot => _expectedStateRoot;

    Hash256 IReadOnlyStateProvider.StateRoot => _expectedStateRoot;

    public IDisposable BeginSystemCallScope() => new SystemCallScope(this);

    public override IDisposable? BeginSystemAccountReadSuppression() =>
        new SystemSuppressionScope(this, base.BeginSystemAccountReadSuppression());

    public new void WarmUp(AccessList? accessList, CancellationToken cancellationToken = default)
    {
        // Closed-World Warm-Up Rule: EIP-2930 tx.accessList warm-ups only mark EVM transient gas warm-sets
        // and must NOT touch the backing Fake Store or record BAL accesses.
    }

    public new void WarmUp(Address address)
    {
        // Closed-World Warm-Up Rule: precompile/coinbase warm-ups must NOT query the Fake Store.
    }

    public override void RecalculateStateRoot()
    {
        // Consumer mode skips Merkle Patricia Trie stateRoot calculation and trusts payload.StateRoot.
    }

    public override void Commit(IReleaseSpec releaseSpec, IWorldStateTracer tracer, bool isGenesis = false, bool commitRoots = true) =>
        base.Commit(releaseSpec, tracer, isGenesis, commitRoots: false);

    public new void CommitTree(ulong blockNumber)
    {
        // Zero persistence in Pre-BAL Consumer mode.
    }

    public override bool TryGetAccount(Address address, out AccountStruct account)
    {
        if (ShouldShortCircuitSuppressedSystemUser(address))
        {
            account = AccountStruct.TotallyEmpty;
            return false;
        }

        EnsureAccountAllowed(address);
        return base.TryGetAccount(address, out account);
    }

    public override ulong GetNonce(Address address)
    {
        if (ShouldShortCircuitSuppressedSystemUser(address))
        {
            return 0UL;
        }

        EnsureAccountAllowed(address);
        return base.GetNonce(address);
    }

    public override ref readonly UInt256 GetBalance(Address address)
    {
        if (ShouldShortCircuitSuppressedSystemUser(address))
        {
            return ref s_zeroBalance;
        }

        EnsureAccountAllowed(address);
        return ref base.GetBalance(address);
    }

    public override ref readonly ValueHash256 GetCodeHash(Address address)
    {
        if (ShouldShortCircuitSuppressedSystemUser(address))
        {
            return ref s_emptyCodeHash;
        }

        EnsureAccountAllowed(address);
        return ref base.GetCodeHash(address);
    }

    public override ReadOnlyMemory<byte> GetCode(Address address)
    {
        if (ShouldShortCircuitSuppressedSystemUser(address))
        {
            return Array.Empty<byte>();
        }

        EnsureAccountAllowed(address);
        return base.GetCode(address);
    }

    public override bool IsContract(Address address)
    {
        if (ShouldShortCircuitSuppressedSystemUser(address))
        {
            return false;
        }

        EnsureAccountAllowed(address);
        return base.IsContract(address);
    }

    public override bool AccountExists(Address address)
    {
        if (ShouldShortCircuitSuppressedSystemUser(address))
        {
            return false;
        }

        EnsureAccountAllowed(address);
        return base.AccountExists(address);
    }

    public override bool IsDeadAccount(Address address)
    {
        if (ShouldShortCircuitSuppressedSystemUser(address))
        {
            return true;
        }

        EnsureAccountAllowed(address);
        return base.IsDeadAccount(address);
    }

    public override UInt256 GetOriginalBalance(Address address)
    {
        if (ShouldShortCircuitSuppressedSystemUser(address))
        {
            return UInt256.Zero;
        }

        EnsureAccountAllowed(address);
        return base.GetOriginalBalance(address);
    }

    public override void GetOriginal(in StorageCell storageCell, out UInt256 value)
    {
        EnsureSlotAllowed(in storageCell);
        base.GetOriginal(in storageCell, out value);
    }

    public override void Get(in StorageCell storageCell, out UInt256 value)
    {
        EnsureSlotAllowed(in storageCell);
        base.Get(in storageCell, out value);
    }

    public override void Set(in StorageCell storageCell, in UInt256 newValue)
    {
        EnsureSlotAllowed(in storageCell);
        base.Set(in storageCell, in newValue);
    }

    public override void Set(in StorageCell storageCell, in UInt256 newValue, in UInt256 currentValue)
    {
        EnsureSlotAllowed(in storageCell);
        base.Set(in storageCell, in newValue, in currentValue);
    }

    public override bool TryGetStorageBeforeClear(in StorageCell storageCell, out UInt256 value)
    {
        EnsureSlotAllowed(in storageCell);
        return base.TryGetStorageBeforeClear(in storageCell, out value);
    }

    public override void ClearStorage(Address address)
    {
        EnsureAccountAllowed(address);
        base.ClearStorage(address);
    }

    public override void DeleteAccount(Address address)
    {
        EnsureAccountAllowed(address);
        base.DeleteAccount(address);
    }

    public override void CreateAccount(Address address, in UInt256 balance, in ulong nonce = default)
    {
        EnsureAccountAllowed(address);
        base.CreateAccount(address, in balance, in nonce);
    }

    public override void CreateAccountIfNotExists(Address address, in UInt256 balance, in ulong nonce = default)
    {
        EnsureAccountAllowed(address);
        base.CreateAccountIfNotExists(address, in balance, in nonce);
    }

    public override void CreateEmptyAccountIfDeleted(Address address)
    {
        EnsureAccountAllowed(address);
        base.CreateEmptyAccountIfDeleted(address);
    }

    public override bool InsertCode(Address address, in ValueHash256 codeHash, ReadOnlyMemory<byte> code, IReleaseSpec spec, bool isGenesis = false)
    {
        EnsureAccountAllowed(address);
        return base.InsertCode(address, in codeHash, code, spec, isGenesis);
    }

    public override void InsertTransientCode(Address address, in ValueHash256 codeHash, ReadOnlyMemory<byte> code, IReleaseSpec spec)
    {
        EnsureAccountAllowed(address);
        base.InsertTransientCode(address, in codeHash, code, spec);
    }

    public override bool AdoptCode(Address address, in ValueHash256 codeHash, ReadOnlyMemory<byte> code, IReleaseSpec spec)
    {
        EnsureAccountAllowed(address);
        return base.AdoptCode(address, in codeHash, code, spec);
    }

    public override void AddToBalance(Address address, in UInt256 balanceChange, IReleaseSpec spec, out UInt256 oldBalance)
    {
        if (ShouldShortCircuitSuppressedSystemUserZeroTransfer(address, in balanceChange))
        {
            oldBalance = UInt256.Zero;
            return;
        }

        EnsureAccountAllowed(address);
        base.AddToBalance(address, in balanceChange, spec, out oldBalance);
    }

    public override bool AddToBalanceAndCreateIfNotExists(Address address, in UInt256 balanceChange, IReleaseSpec spec, out UInt256 oldBalance)
    {
        if (ShouldShortCircuitSuppressedSystemUserZeroTransfer(address, in balanceChange))
        {
            oldBalance = UInt256.Zero;
            return false;
        }

        EnsureAccountAllowed(address);
        return base.AddToBalanceAndCreateIfNotExists(address, in balanceChange, spec, out oldBalance);
    }

    public override void SubtractFromBalance(Address address, in UInt256 balanceChange, IReleaseSpec spec, out UInt256 oldBalance)
    {
        if (_systemCallDepth > 0 && address == Address.SystemUser && balanceChange.IsZero && !_preBal.ContainsAccount(address))
        {
            oldBalance = UInt256.Zero;
            return;
        }

        EnsureAccountAllowed(address);
        base.SubtractFromBalance(address, in balanceChange, spec, out oldBalance);
    }

    public override void IncrementNonce(Address address, ulong delta, out ulong oldNonce)
    {
        EnsureAccountAllowed(address);
        base.IncrementNonce(address, delta, out oldNonce);
    }

    public override void DecrementNonce(Address address, ulong delta)
    {
        EnsureAccountAllowed(address);
        base.DecrementNonce(address, delta);
    }

    public override void SetNonce(Address address, in ulong nonce)
    {
        EnsureAccountAllowed(address);
        base.SetNonce(address, in nonce);
    }

    public override void AddAccountRead(Address address)
    {
        if (_systemAccountReadSuppressionDepth > 0 && address == Address.SystemUser)
        {
            return;
        }

        EnsureAccountAllowed(address);
        base.AddAccountRead(address);
    }

    public override void RecordAccountAccess(Address address)
    {
        if (_systemAccountReadSuppressionDepth > 0 && address == Address.SystemUser)
        {
            return;
        }

        EnsureAccountAllowed(address);
        base.RecordAccountAccess(address);
    }

    public override void RecordBytecodeAccess(Address address)
    {
        EnsureAccountAllowed(address);
        base.RecordBytecodeAccess(address);
    }

    private bool ShouldShortCircuitSuppressedSystemUser(Address address) =>
        _systemAccountReadSuppressionDepth > 0 && address == Address.SystemUser && !_preBal.ContainsAccount(address);

    private bool ShouldShortCircuitSuppressedSystemUserZeroTransfer(Address address, in UInt256 balanceChange) =>
        _systemAccountReadSuppressionDepth > 0 && address == Address.SystemUser && balanceChange.IsZero && !_preBal.ContainsAccount(address);

    private void EnsureAccountAllowed(Address address)
    {
        if (!_preBal.ContainsAccount(address))
        {
            throw new MissingPreBalStateException($"Account {address} accessed during execution is missing from pre_bal.");
        }
    }

    private void EnsureSlotAllowed(in StorageCell storageCell)
    {
        if (!_preBal.TryGetAccount(storageCell.Address, out PreAccountState? account))
        {
            throw new MissingPreBalStateException($"Account {storageCell.Address} accessed during storage operation is missing from pre_bal.");
        }

        if (!account.ContainsSlot(in storageCell.Index))
        {
            throw new MissingPreBalStateException($"Storage slot {storageCell.Index} on account {storageCell.Address} accessed during execution is missing from pre_bal.");
        }
    }

    private sealed class SystemSuppressionScope : IDisposable
    {
        private PreBalWorldState? _owner;
        private IDisposable? _inner;

        public SystemSuppressionScope(PreBalWorldState owner, IDisposable? inner)
        {
            _owner = owner;
            _inner = inner;
            owner._systemAccountReadSuppressionDepth++;
        }

        public void Dispose()
        {
            PreBalWorldState? owner = _owner;
            if (owner is null)
            {
                return;
            }

            _owner = null;
            owner._systemAccountReadSuppressionDepth--;
            _inner?.Dispose();
            _inner = null;
        }
    }

    private sealed class SystemCallScope : IDisposable
    {
        private PreBalWorldState? _owner;

        public SystemCallScope(PreBalWorldState owner)
        {
            _owner = owner;
            owner._systemCallDepth++;
        }

        public void Dispose()
        {
            PreBalWorldState? owner = _owner;
            if (owner is null)
            {
                return;
            }

            _owner = null;
            owner._systemCallDepth--;
        }
    }
}

/// <summary>
/// Closed-world <see cref="IBlockhashProvider"/> that serves in-range <c>BLOCKHASH</c> queries strictly from
/// <see cref="PreBlockAccessList.BlockHashes"/> and records every queried in-range block number.
/// </summary>
internal sealed class PreBalBlockhashProvider(PreBlockAccessList preBal) : IBlockhashProvider
{
    private const ulong MaxBlockhashDepth = 256UL;
    private readonly HashSet<ulong> _accessedBlockNumbers = [];

    /// <summary>Gets the set of in-range block numbers queried via <c>BLOCKHASH</c> during execution.</summary>
    public HashSet<ulong> AccessedBlockNumbers => _accessedBlockNumbers;

    public Hash256? GetBlockhash(BlockHeader currentBlock, ulong number, IReleaseSpec spec)
    {
        if (number >= currentBlock.Number || currentBlock.Number - number > MaxBlockhashDepth)
        {
            return null;
        }

        _accessedBlockNumbers.Add(number);

        if (!preBal.TryGetBlockHash(number, out Hash256? hash))
        {
            throw new MissingPreBalBlockHashException(number);
        }

        return hash;
    }

    public Task Prefetch(BlockHeader currentBlock, CancellationToken token) => Task.CompletedTask;
}
