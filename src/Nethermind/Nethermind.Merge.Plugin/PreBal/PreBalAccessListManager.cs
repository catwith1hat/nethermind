// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using Nethermind.Blockchain.BeaconBlockRoot;
using Nethermind.Blockchain.Blocks;
using Nethermind.Blockchain.Tracing;
using Nethermind.Consensus.ExecutionRequests;
using Nethermind.Consensus.Processing;
using Nethermind.Consensus.Withdrawals;
using Nethermind.Core;
using Nethermind.Core.BlockAccessLists;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Evm;
using Nethermind.Evm.State;
using Nethermind.Evm.Tracing;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Serialization.Rlp.Eip7928;

namespace Nethermind.Merge.Plugin.PreBal;

/// <summary>
/// Sequential <see cref="IBlockAccessListManager"/> that constructs the canonical EIP-7928
/// <see cref="GeneratedBlockAccessList"/> across ALL forks (including pre-Amsterdam forks such as Paris,
/// Shanghai, Cancun, Prague, and Osaka) for Pre-BAL exact-match verification.
/// </summary>
internal sealed class PreBalAccessListManager(
    PreBalWorldState worldState,
    TracedAccessWorldState tracedWorldState,
    ITransactionProcessor txProcessor,
    ZeroNonceStorageAccountsTransition zeroNonceTransition,
    ILogManager logManager) : IBlockAccessListManager
{
    private readonly PreBalWorldState _worldState = worldState;
    private readonly TracedAccessWorldState _tracedWorldState = tracedWorldState;
    private readonly ITransactionProcessor _txProcessor = txProcessor;
    private readonly ITransactionProcessorAdapter _txProcessorAdapter = new ExecuteTransactionProcessorAdapter(txProcessor);
    private readonly BeaconBlockRootHandler _beaconBlockRootHandler = new(txProcessor, worldState);
    private readonly BlockhashStore _blockhashStore = new(worldState);
    private readonly WithdrawalProcessor _withdrawalProcessor = new(worldState, logManager);
    private readonly ExecutionRequestsProcessor _executionRequestsProcessor = new(txProcessor);
    private readonly ZeroNonceStorageAccountsTransition _zeroNonceTransition = zeroNonceTransition;
    private bool _blockLevelAccessListsEnabled;

    public GeneratedBlockAccessList GeneratedBlockAccessList { get; set; } = new();

    public bool Enabled { get; private set; }

    public bool ParallelExecutionEnabled => false;

    public bool BatchReadEnabled => false;

    public bool ForceConstructGeneratedBlockAccessList { get; set; } = true;

    public void PrepareForProcessing(Block suggestedBlock, IReleaseSpec spec, ProcessingOptions options)
    {
        Enabled = !suggestedBlock.IsGenesis;
        _blockLevelAccessListsEnabled = spec.BlockLevelAccessListsEnabled;
        GeneratedBlockAccessList.Reset();
    }

    public void WaitForBalWarmup() { }

    public void Setup(Block block)
    {
        _tracedWorldState.Clear();
        _tracedWorldState.SetIndex(0u);
    }

    public void SpendGas(ulong gas) { }

    public void SetBlockExecutionContext(in BlockExecutionContext blockExecutionContext) =>
        _txProcessorAdapter.SetBlockExecutionContext(in blockExecutionContext);

    public ITransactionProcessorAdapter GetTxProcessor(uint? balIndex = null) => _txProcessorAdapter;

    public void NextTransaction()
    {
        if (!Enabled)
        {
            return;
        }

        MergeCurrentSlice();
        _tracedWorldState.Clear();
        _tracedWorldState.IncrementIndex();
    }

    public void Rollback() => _tracedWorldState.Clear();

    public void ReturnTxProcessor(uint balIndex) { }

    public void IncrementalValidation(
        Block block,
        GasValidationResultSlot[] gasResults,
        BlockReceiptsTracer[] receiptsTracers,
        BlockProcessor.BlockValidationTransactionsExecutor.ITransactionProcessedEventHandler? transactionProcessedEventHandler,
        CancellationToken token) =>
        throw new NotSupportedException("Parallel incremental validation is disabled in Pre-BAL consumer mode.");

    public void SetBlockAccessList(Block block)
    {
        if (!Enabled)
        {
            return;
        }

        MergeCurrentSlice();
        block.GeneratedBlockAccessList = GeneratedBlockAccessList;

        if (_blockLevelAccessListsEnabled)
        {
            block.EncodedBlockAccessList = BlockAccessListDecoder.EncodeToBytes(GeneratedBlockAccessList);
            block.Header.BlockAccessListHash = new Hash256(ValueKeccak.Compute(block.EncodedBlockAccessList));
        }
    }

    public void ValidateBlockAccessList(Block block, uint index, bool validateStorageReads = true)
    {
        // Full canonical EIP-7928 BAL equality (hash + wire bytes) and Pre-BAL exact-match verification
        // run deterministically once SetBlockAccessList completes at the end of the block.
    }

    public void StoreBeaconRoot(Block block, IReleaseSpec spec)
    {
        using IDisposable systemCallScope = _worldState.BeginSystemCallScope();
        _beaconBlockRootHandler.StoreBeaconRoot(block, spec, NullTxTracer.Instance);
    }

    public void ApplyBlockhashStateChanges(BlockHeader header, IReleaseSpec spec)
    {
        using IDisposable systemCallScope = _worldState.BeginSystemCallScope();
        if (!spec.IsEip8037Enabled)
        {
            if (_blockhashStore.TryGetHistoryContract(header, spec, out Address? historyContract))
            {
                // Ensure the EIP-2935 ring-buffer slot is recorded in StorageReads even when preValue == parentHash.
                StorageCell parentHashCell = new(historyContract, new UInt256((ulong)(header.Number - 1) % spec.Eip2935RingBufferSize));
                _worldState.Get(in parentHashCell, out _);
            }

            _blockhashStore.ApplyBlockhashStateChanges(header, spec);
            return;
        }

        if (!_blockhashStore.TryGetHistoryContract(header, spec, out Address? contract))
        {
            return;
        }

        SystemCall transaction = new()
        {
            GasLimit = Eip8037Constants.SystemCallGasLimit,
            Data = header.ParentHash!.Bytes.ToArray(),
            To = contract,
            SenderAddress = Address.SystemUser,
        };
        _txProcessor.Execute(transaction, NullTxTracer.Instance);
    }

    public void ApplyZeroNonceStorageAccountsTransition(BlockHeader header, IReleaseSpec spec) =>
        _zeroNonceTransition.ApplyIfForkBlock(header, spec, _worldState);

    public void ProcessWithdrawals(Block block, IReleaseSpec spec) =>
        _withdrawalProcessor.ProcessWithdrawals(block, spec);

    public void ProcessExecutionRequests(Block block, TxReceipt[] txReceipts, IReleaseSpec spec)
    {
        using IDisposable systemCallScope = _worldState.BeginSystemCallScope();
        _executionRequestsProcessor.ProcessExecutionRequests(block, _worldState, txReceipts, spec);
    }

    private void MergeCurrentSlice()
    {
        BlockAccessListAtIndex? slice = _tracedWorldState.GetGeneratingBlockAccessList();
        if (slice is not null)
        {
            GeneratedBlockAccessList.Merge(slice);
        }
    }
}
