// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using Nethermind.Blockchain;
using Nethermind.Blockchain.BeaconBlockRoot;
using Nethermind.Blockchain.Blocks;
using Nethermind.Blockchain.Receipts;
using Nethermind.Blockchain.Tracing;
using Nethermind.Consensus;
using Nethermind.Consensus.ExecutionRequests;
using Nethermind.Consensus.Processing;
using Nethermind.Consensus.Rewards;
using Nethermind.Consensus.Stateless;
using Nethermind.Consensus.Validators;
using Nethermind.Consensus.Withdrawals;
using Nethermind.Core;
using Nethermind.Core.BlockAccessLists;
using Nethermind.Core.Crypto;
using Nethermind.Core.Exceptions;
using Nethermind.Core.Messages;
using Nethermind.Core.Specs;
using Nethermind.Crypto;
using Nethermind.Evm;
using Nethermind.Evm.State;
using Nethermind.Evm.Tracing;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Int256;
using Nethermind.JsonRpc;
using Nethermind.Logging;
using Nethermind.Merge.Plugin.Data;
using Nethermind.Serialization.FluentRlp;
using Nethermind.Serialization.Rlp;
using Nethermind.State;
using Nethermind.TxPool;
using ValidationResult = Nethermind.Merge.Plugin.Data.ValidationResult;

namespace Nethermind.Merge.Plugin.PreBal;

/// <summary>
/// Stateless Pre-BAL Consumer execution and exact-match verification pipeline.
/// </summary>
internal static class PreBalExecutionHandler
{
    private const int CodeCacheCapacity = 512;
    private const int MaxPoSExtraDataBytes = 32;

    /// <summary>
    /// Validates <paramref name="preBalBytes"/> and executes <paramref name="executionPayloadParams"/> against
    /// an isolated closed-world in-memory Fake Store with zero persistence.
    /// </summary>
    public static ResultWrapper<PayloadStatusV1> Execute(
        ISpecProvider specProvider,
        ILogManager logManager,
        IExecutionPayloadParams executionPayloadParams,
        int version,
        byte[] preBalBytes)
    {
        ILogger logger = logManager.GetClassLogger(typeof(PreBalExecutionHandler));
        ExecutionPayload executionPayload = executionPayloadParams.ExecutionPayload;
        executionPayload.ExecutionRequests = executionPayloadParams.ExecutionRequests;
        executionPayload.InclusionListTransactions = executionPayloadParams.InclusionListTransactions;

        if (!executionPayload.ValidateForkOnNewPayload(specProvider, version))
        {
            if (executionPayload.HasUnboundField)
            {
                string unboundError = executionPayload.UnboundFieldError;
                if (logger.IsWarn) logger.Warn(unboundError);
                return ResultWrapper<PayloadStatusV1>.Fail(unboundError, ErrorCodes.InvalidParams);
            }

            if (logger.IsWarn) logger.Warn("The payload is not supported by the current fork");
            return ResultWrapper<PayloadStatusV1>.Fail(
                MergeErrorMessages.UnsupportedFork,
                version < EngineApiVersions.NewPayload.V2 ? ErrorCodes.InvalidParams : MergeErrorCodes.UnsupportedFork);
        }

        IReleaseSpec releaseSpec = specProvider.GetSpec(executionPayload.BlockNumber, executionPayload.Timestamp);
        if (executionPayload.LogsBloom is { IsZeroLength: true } && !releaseSpec.IsEip7668Enabled)
        {
            executionPayload.LogsBloom = new Bloom();
        }

        PreBlockAccessList preBal;
        try
        {
            preBal = PreBlockAccessListDecoder.Decode(preBalBytes);
        }
        catch (RlpReaderException ex)
        {
            if (logger.IsWarn) logger.Warn($"Invalid pre_bal RLP: {ex.Message}");
            return ResultWrapper<PayloadStatusV1>.Fail($"Invalid pre_bal RLP: {ex.Message}", ErrorCodes.InvalidParams);
        }

        if (executionPayload.ParentHash is null
            || ValueKeccak.Compute(preBal.ParentHeader) != executionPayload.ParentHash.ValueHash256)
        {
            const string mismatchMsg = "pre_bal parent_header hash does not match executionPayload.parentHash";
            if (logger.IsWarn) logger.Warn(mismatchMsg);
            return ResultWrapper<PayloadStatusV1>.Fail(mismatchMsg, ErrorCodes.InvalidParams);
        }

        BlockHeader parentHeader;
        try
        {
            HeaderDecoder headerDecoder = new(specProvider);
            RlpReader headerReader = new(preBal.ParentHeader);
            BlockHeader? decodedParent = headerDecoder.Decode(ref headerReader, RlpBehaviors.None);
            if (decodedParent is null || headerReader.HasNext)
            {
                return ResultWrapper<PayloadStatusV1>.Fail("Malformed parent_header RLP in pre_bal", ErrorCodes.InvalidParams);
            }

            parentHeader = decodedParent;
        }
        catch (RlpReaderException ex)
        {
            if (logger.IsWarn) logger.Warn($"Malformed parent_header RLP in pre_bal: {ex.Message}");
            return ResultWrapper<PayloadStatusV1>.Fail($"Malformed parent_header RLP in pre_bal: {ex.Message}", ErrorCodes.InvalidParams);
        }

        Hash256 parentHash = parentHeader.Hash!;

        ValidationResult paramValidation = executionPayloadParams.ValidateParams(
            releaseSpec,
            version,
            transactionCountGuard: null,
            out string? paramError,
            out _);

        if (paramValidation == ValidationResult.Fail)
        {
            if (logger.IsWarn) logger.Warn(paramError!);
            return ResultWrapper<PayloadStatusV1>.Fail(paramError!, ErrorCodes.InvalidParams);
        }

        SyncParentBeaconBlockRoot(executionPayloadParams, executionPayload);

        Result<Block> blockDecodingResult = executionPayload.TryGetBlock();
        if (blockDecodingResult.IsError)
        {
            return NewPayloadV1Result.Invalid(parentHash, $"Block could not be parsed: {blockDecodingResult.Error}");
        }

        Block suggestedBlock = blockDecodingResult.Data;

        if (!HeaderValidator.ValidateHash(suggestedBlock.Header, out Hash256 actualHash))
        {
            string hashError = $"Invalid block hash {executionPayload.BlockHash} does not match calculated hash {actualHash}.";
            if (version == EngineApiVersions.NewPayload.V1)
            {
                return ResultWrapper<PayloadStatusV1>.Success(new PayloadStatusV1
                {
                    Status = PayloadStatus.InvalidBlockHash,
                    LatestValidHash = null,
                    ValidationError = hashError
                });
            }

            return NewPayloadV1Result.Invalid(null, hashError);
        }

        if (paramValidation != ValidationResult.Success)
        {
            return NewPayloadV1Result.Invalid(parentHash, paramError);
        }

        if (!TryRecoverTransactionSenders(suggestedBlock.Transactions, specProvider, releaseSpec, out string? senderError))
        {
            return NewPayloadV1Result.Invalid(parentHash, senderError);
        }

        StatelessBlockTree statelessBlockTree = new([parentHeader]);
        TxValidator txValidator = new(specProvider.ChainId);
        PreBalHeaderValidator headerValidator = new(statelessBlockTree, Always.Valid, specProvider, logManager);
        BlockValidator blockValidator = new(
            txValidator,
            headerValidator,
            new UnclesValidator(statelessBlockTree, headerValidator, logManager),
            specProvider,
            logManager);

        if (!blockValidator.ValidateSuggestedBlock(suggestedBlock, parentHeader, out string? headerOrBodyError, validateHashes: false))
        {
            return NewPayloadV1Result.Invalid(parentHash, headerOrBodyError);
        }

        PreBalScopeProvider scopeProvider = new(preBal, parentHeader.StateRoot ?? Keccak.EmptyTreeHash);
        WorldState innerWorldState = new(scopeProvider, logManager);
        if (!innerWorldState.TryBeginScope(parentHeader, out IDisposable? scopeCloser))
        {
            return NewPayloadV1Result.Invalid(parentHash, "Failed to open Pre-BAL world state scope.");
        }

        using IDisposable worldStateScope = scopeCloser;
        TracedAccessWorldState tracedWorldState = new(innerWorldState, parallel: false);
        tracedWorldState.SetGeneratingBlockAccessList(new BlockAccessListAtIndex());

        PreBalWorldState worldState = new(tracedWorldState, preBal, suggestedBlock.Header.StateRoot!);
        PreBalBlockhashProvider blockhashProvider = new(preBal);
        StaticCodeCache codeCache = new(CodeCacheCapacity);
        CacheCodeInfoRepository codeInfoRepository = new(worldState, new EthereumPrecompileProvider(), codeCache);
        EthereumVirtualMachine virtualMachine = new(blockhashProvider, specProvider, logManager);
        using IDisposable vmDisposable = virtualMachine;
        EthereumTransactionProcessor txProcessor = new(
            BlobBaseFeeCalculator.Instance,
            specProvider,
            worldState,
            virtualMachine,
            codeInfoRepository,
            logManager);

        ZeroNonceStorageAccountsTransition zeroNonceTransition = new(specProvider, statelessBlockTree);
        PreBalAccessListManager balManager = new(
            worldState,
            tracedWorldState,
            txProcessor,
            zeroNonceTransition,
            logManager);

        BlockProcessor.ParallelBlockValidationTransactionsExecutor txExecutor = new(
            new BlockProcessor.BlockValidationTransactionsExecutor(
                new ExecuteTransactionProcessorAdapter(txProcessor),
                worldState),
            worldState,
            specProvider,
            balManager,
            logManager);

        BlockProcessor blockProcessor = new(
            specProvider,
            blockValidator,
            NoBlockRewards.Instance,
            txExecutor,
            worldState,
            NullReceiptStorage.Instance,
            new BeaconBlockRootHandler(txProcessor, worldState),
            new BlockhashStore(worldState),
            logManager,
            new WithdrawalProcessor(worldState, logManager),
            new ExecutionRequestsProcessor(txProcessor),
            balManager);

        bool inclusionListSatisfied = true;
        if (releaseSpec.InclusionListsEnabled && suggestedBlock.InclusionListTransactions is { Length: > 0 } ilTxs)
        {
            RecoverInclusionListSenders(ilTxs, specProvider, releaseSpec);
            blockProcessor.TransactionsExecuted += processed =>
                inclusionListSatisfied = InclusionListValidator.IsSatisfied(processed, ilTxs, innerWorldState, releaseSpec, txValidator);
        }

        Block processedBlock;
        TxReceipt[] receipts;
        try
        {
            (processedBlock, receipts) = blockProcessor.ProcessOne(
                suggestedBlock,
                ProcessingOptions.ReadOnlyChain,
                NullBlockTracer.Instance,
                releaseSpec,
                CancellationToken.None);
        }
        catch (MissingPreBalStateException ex)
        {
            return NewPayloadV1Result.Invalid(parentHash, ex.Message);
        }
        catch (MissingPreBalBlockHashException ex)
        {
            return NewPayloadV1Result.Invalid(parentHash, ex.Message);
        }
        catch (InvalidBlockException ex)
        {
            return NewPayloadV1Result.Invalid(parentHash, ex.Message);
        }

        if (!blockValidator.ValidateProcessedBlock(processedBlock, receipts, suggestedBlock, out string? processedError))
        {
            return NewPayloadV1Result.Invalid(parentHash, processedError);
        }

        if (releaseSpec.RequestsEnabled && !ExecutionRequestsMatch(processedBlock.ExecutionRequests, executionPayloadParams.ExecutionRequests))
        {
            return NewPayloadV1Result.Invalid(parentHash, "Execution requests mismatch.");
        }

        if (releaseSpec.BlockLevelAccessListsEnabled
            && !processedBlock.EncodedBlockAccessList.AsSpan().SequenceEqual(executionPayload.BlockAccessList))
        {
            return NewPayloadV1Result.Invalid(parentHash, "Computed EIP-7928 blockAccessList does not match payload blockAccessList.");
        }

        if (!VerifyExactMatch(preBal, balManager.GeneratedBlockAccessList, blockhashProvider.AccessedBlockNumbers, out string? exactMatchError))
        {
            return NewPayloadV1Result.Invalid(parentHash, exactMatchError);
        }

        return inclusionListSatisfied
            ? NewPayloadV1Result.Valid(suggestedBlock.Hash)
            : NewPayloadV1Result.InclusionListUnsatisfied(suggestedBlock.Hash);
    }

    private static void SyncParentBeaconBlockRoot(IExecutionPayloadParams payloadParams, ExecutionPayload payload)
    {
        if (payloadParams is ExecutionPayloadParams<ExecutionPayloadV4> v4Params)
        {
            payload.ParentBeaconBlockRoot = v4Params.ParentBeaconBlockRoot;
        }
        else if (payloadParams is ExecutionPayloadParams<ExecutionPayloadV3> v3Params)
        {
            payload.ParentBeaconBlockRoot = v3Params.ParentBeaconBlockRoot;
        }
    }

    private static bool TryRecoverTransactionSenders(
        Transaction[] transactions,
        ISpecProvider specProvider,
        IReleaseSpec spec,
        out string? error)
    {
        error = null;
        if (transactions.Length == 0)
        {
            return true;
        }

        EthereumEcdsa ecdsa = new(specProvider.ChainId);
        for (int i = 0; i < transactions.Length; i++)
        {
            Transaction tx = transactions[i];
            if (tx.SenderAddress is not null || tx.SupportsFrames)
            {
                continue;
            }

            Address? sender = ecdsa.RecoverAddress(tx, !spec.ValidateChainId);
            if (sender is null)
            {
                error = $"Failed to recover sender signature for transaction at index {i}.";
                return false;
            }

            tx.SenderAddress = sender;
        }

        return true;
    }

    private static void RecoverInclusionListSenders(Transaction[] transactions, ISpecProvider specProvider, IReleaseSpec spec)
    {
        EthereumEcdsa ecdsa = new(specProvider.ChainId);
        for (int i = 0; i < transactions.Length; i++)
        {
            Transaction tx = transactions[i];
            if (tx.SenderAddress is null && !tx.SupportsFrames && tx.Signature is not null)
            {
                tx.SenderAddress = ecdsa.RecoverAddress(tx, !spec.ValidateChainId);
            }
        }
    }

    private static bool ExecutionRequestsMatch(byte[][]? actual, byte[][]? expected)
    {
        if (actual is null || expected is null)
        {
            return actual is null && expected is null;
        }

        if (actual.Length != expected.Length)
        {
            return false;
        }

        for (int i = 0; i < actual.Length; i++)
        {
            if (!actual[i].AsSpan().SequenceEqual(expected[i]))
            {
                return false;
            }
        }

        return true;
    }

    private static bool VerifyExactMatch(
        PreBlockAccessList preBal,
        GeneratedBlockAccessList generatedBal,
        System.Collections.Generic.HashSet<ulong> accessedBlockNumbers,
        out string? error)
    {
        PreAccountState[] preAccounts = preBal.Accounts;
        for (int i = 0; i < preAccounts.Length; i++)
        {
            PreAccountState preAcc = preAccounts[i];
            GeneratedAccountChanges? genAcc = generatedBal.GetAccountChanges(preAcc.Address);
            if (genAcc is null)
            {
                error = $"Unused account {preAcc.Address} in pre_bal.";
                return false;
            }

            PreStorageSlot[] preSlots = preAcc.Storage;
            for (int j = 0; j < preSlots.Length; j++)
            {
                UInt256 key = preSlots[j].Key;
                if (!genAcc.TryGetSlotChanges(key, out _) && !genAcc.StorageReads.Contains(key))
                {
                    error = $"Unused storage slot {key} for account {preAcc.Address} in pre_bal.";
                    return false;
                }
            }

            int generatedSlotCount = genAcc.StorageChanges.Count + genAcc.StorageReads.Count;
            if (generatedSlotCount != preSlots.Length)
            {
                error = $"Missing storage slot for account {preAcc.Address} in pre_bal (expected {generatedSlotCount}, got {preSlots.Length}).";
                return false;
            }
        }

        if (generatedBal.AccountChanges.Count != preAccounts.Length)
        {
            foreach (GeneratedAccountChanges genAcc in generatedBal.AccountChanges)
            {
                if (!preBal.ContainsAccount(genAcc.Address))
                {
                    error = $"Missing account {genAcc.Address} in pre_bal.";
                    return false;
                }
            }

            error = $"Account count mismatch between execution ({generatedBal.AccountChanges.Count}) and pre_bal ({preAccounts.Length}).";
            return false;
        }

        PreBlockHash[] preHashes = preBal.BlockHashes;
        for (int i = 0; i < preHashes.Length; i++)
        {
            if (!accessedBlockNumbers.Contains(preHashes[i].BlockNumber))
            {
                error = $"Unused block hash for block number {preHashes[i].BlockNumber} in pre_bal.";
                return false;
            }
        }

        if (accessedBlockNumbers.Count != preHashes.Length)
        {
            error = $"Missing block hash in pre_bal (queried {accessedBlockNumbers.Count}, supplied {preHashes.Length}).";
            return false;
        }

        error = null;
        return true;
    }

    private sealed class PreBalHeaderValidator(
        IBlockTree blockTree,
        ISealValidator sealValidator,
        ISpecProvider specProvider,
        ILogManager logManager)
        : HeaderValidator(blockTree, sealValidator, specProvider, logManager)
    {
        protected override bool Validate<TOrphaned>(BlockHeader header, BlockHeader? parent, bool isUncle, out string? error, bool validateHash)
        {
            error = null;
            if (!header.Difficulty.IsZero)
            {
                error = "Post-merge block difficulty must be zero.";
                return false;
            }

            if (header.Nonce != 0)
            {
                error = "Post-merge block nonce must be zero.";
                return false;
            }

            if (header.UnclesHash != Keccak.OfAnEmptySequenceRlp)
            {
                error = BlockErrorMessages.InvalidUnclesHash;
                return false;
            }

            return base.Validate<TOrphaned>(header, parent, isUncle, out error, validateHash);
        }

        protected override bool ValidateTotalDifficulty(BlockHeader header, BlockHeader parent, ref string? error) => true;

        protected override bool ValidateExtraData(BlockHeader header, IReleaseSpec spec, bool isUncle, ref string? error)
        {
            if (header.ExtraData.Length > MaxPoSExtraDataBytes)
            {
                error = BlockErrorMessages.InvalidExtraData;
                return false;
            }

            return true;
        }
    }
}
