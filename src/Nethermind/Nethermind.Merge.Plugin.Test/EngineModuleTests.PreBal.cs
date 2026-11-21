// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Blockchain;
using Nethermind.Blockchain.BeaconBlockRoot;
using Nethermind.Blockchain.Blocks;
using Nethermind.Blockchain.Receipts;
using Nethermind.Blockchain.Tracing;
using Nethermind.Consensus.ExecutionRequests;
using Nethermind.Consensus.Processing;
using Nethermind.Consensus.Producers;
using Nethermind.Consensus.Rewards;
using Nethermind.Consensus.Stateless;
using Nethermind.Consensus.Validators;
using Nethermind.Consensus.Withdrawals;
using Nethermind.Core;
using Nethermind.Core.BlockAccessLists;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Eip2930;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.Evm;
using Nethermind.Evm.State;
using Nethermind.Evm.Tracing;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Int256;
using Nethermind.JsonRpc;
using Nethermind.JsonRpc.Test;
using Nethermind.Logging;
using Nethermind.Merge.Plugin.Data;
using Nethermind.Merge.Plugin.PreBal;
using Nethermind.Serialization.FluentRlp;
using Nethermind.Serialization.Rlp;
using Nethermind.Specs;
using Nethermind.Specs.Forks;
using Nethermind.State;
using Nethermind.TxPool;
using NUnit.Framework;

namespace Nethermind.Merge.Plugin.Test;

public partial class EngineModuleTests
{
    [Test]
    public void PreBalDecoder_roundtrips_canonical_rlp()
    {
        BlockHeader parentHeader = Build.A.BlockHeader
            .WithNumber(42)
            .WithTimestamp(1_700_000_000)
            .WithDifficulty(0)
            .WithBaseFee(7.GWei)
            .TestObject;
        byte[] parentHeaderRlp = Rlp.Encode(parentHeader);

        PreAccountState emptyAcc = new(
            new Address("0x0000000000000000000000000000000000000001"),
            exists: false,
            nonce: 0,
            balance: UInt256.Zero,
            code: [],
            storage: []);

        PreAccountState contractAcc = new(
            new Address("0x1111111111111111111111111111111111111111"),
            exists: true,
            nonce: 3,
            balance: 12345.Ether,
            code: [0x60, 0x00, 0x54, 0x00],
            storage:
            [
                new PreStorageSlot(UInt256.Zero, UInt256.Zero),
                new PreStorageSlot(UInt256.One, new UInt256(999)),
            ]);

        PreBlockAccessList original = new(
            parentHeaderRlp,
            [emptyAcc, contractAcc],
            [
                new PreBlockHash(40, TestItem.KeccakA),
                new PreBlockHash(41, TestItem.KeccakB),
            ]);

        byte[] encoded = PreBlockAccessListDecoder.Encode(original);
        PreBlockAccessList decoded = PreBlockAccessListDecoder.Decode(encoded);
        byte[] reencoded = PreBlockAccessListDecoder.Encode(decoded);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(reencoded, Is.EqualTo(encoded));
            Assert.That(decoded.ParentHeader, Is.EqualTo(parentHeaderRlp));
            Assert.That(decoded.Accounts, Has.Length.EqualTo(2));
            Assert.That(decoded.Accounts[0].Exists, Is.False);
            Assert.That(decoded.Accounts[1].Exists, Is.True);
            Assert.That(decoded.Accounts[1].Nonce, Is.EqualTo(3UL));
            Assert.That(decoded.Accounts[1].Balance, Is.EqualTo(12345.Ether));
            Assert.That(decoded.Accounts[1].Code, Is.EqualTo(new byte[] { 0x60, 0x00, 0x54, 0x00 }));
            Assert.That(decoded.Accounts[1].Storage, Has.Length.EqualTo(2));
            Assert.That(decoded.BlockHashes, Has.Length.EqualTo(2));
        }
    }

    [Test]
    public void PreBalDecoder_rejects_non_canonical_and_unsorted_inputs()
    {
        byte[] validParentRlp = Rlp.Encode(Build.A.BlockHeader.WithNumber(1).WithDifficulty(0).TestObject);
        Address addrA = new("0x000000000000000000000000000000000000000a");
        Address addrB = new("0x000000000000000000000000000000000000000b");

        PreAccountState accA = new(addrA, true, 1, UInt256.Zero, [], []);
        PreAccountState accB = new(addrB, true, 1, UInt256.Zero, [], []);

        // 1. Unsorted accounts
        byte[] unsortedAccounts = PreBlockAccessListDecoder.Encode(new PreBlockAccessList(validParentRlp, [accB, accA], []));
        Assert.Throws<RlpReaderException>(() => PreBlockAccessListDecoder.Decode(unsortedAccounts));

        // 2. Duplicate accounts
        byte[] duplicateAccounts = PreBlockAccessListDecoder.Encode(new PreBlockAccessList(validParentRlp, [accA, accA], []));
        Assert.Throws<RlpReaderException>(() => PreBlockAccessListDecoder.Decode(duplicateAccounts));

        // 3. Unsorted storage keys
        PreAccountState unsortedStorageAcc = new(addrA, true, 1, UInt256.Zero, [],
            [new PreStorageSlot(new UInt256(5), UInt256.Zero), new PreStorageSlot(new UInt256(2), UInt256.Zero)]);
        byte[] unsortedStorage = PreBlockAccessListDecoder.Encode(new PreBlockAccessList(validParentRlp, [unsortedStorageAcc], []));
        Assert.Throws<RlpReaderException>(() => PreBlockAccessListDecoder.Decode(unsortedStorage));

        // 4. Duplicate storage keys
        PreAccountState dupStorageAcc = new(addrA, true, 1, UInt256.Zero, [],
            [new PreStorageSlot(UInt256.One, UInt256.Zero), new PreStorageSlot(UInt256.One, UInt256.One)]);
        byte[] dupStorage = PreBlockAccessListDecoder.Encode(new PreBlockAccessList(validParentRlp, [dupStorageAcc], []));
        Assert.Throws<RlpReaderException>(() => PreBlockAccessListDecoder.Decode(dupStorage));

        // 5. Unsorted / duplicate block hashes
        byte[] unsortedHashes = PreBlockAccessListDecoder.Encode(new PreBlockAccessList(
            validParentRlp, [], [new PreBlockHash(10, TestItem.KeccakA), new PreBlockHash(9, TestItem.KeccakB)]));
        Assert.Throws<RlpReaderException>(() => PreBlockAccessListDecoder.Decode(unsortedHashes));

        // 6. EIP-161 violation: exists == false with non-zero balance
        PreAccountState nonEmptyNonExistent = new(addrA, false, 0, UInt256.One, [], []);
        byte[] invalidFalseExists = PreBlockAccessListDecoder.Encode(new PreBlockAccessList(validParentRlp, [nonEmptyNonExistent], []));
        Assert.Throws<RlpReaderException>(() => PreBlockAccessListDecoder.Decode(invalidFalseExists));

        // 7. EIP-161 violation: exists == true with all-zero fields
        PreAccountState emptyExistent = new(addrA, true, 0, UInt256.Zero, [], []);
        byte[] invalidTrueExists = PreBlockAccessListDecoder.Encode(new PreBlockAccessList(validParentRlp, [emptyExistent], []));
        Assert.Throws<RlpReaderException>(() => PreBlockAccessListDecoder.Decode(invalidTrueExists));

        // 8. Trailing bytes after outer sequence
        byte[] valid = PreBlockAccessListDecoder.Encode(new PreBlockAccessList(validParentRlp, [accA], []));
        byte[] withTrailing = [.. valid, 0x00];
        Assert.Throws<RlpReaderException>(() => PreBlockAccessListDecoder.Decode(withTrailing));

        // 9. Non-canonical exists = 0x00 instead of 0x80
        byte[] rawInvalidBool = RlpCodec.Write(validParentRlp, static (ref RlpWriter w, byte[] p) =>
        {
            w.WriteSequence(p, static (ref RlpWriter rw, byte[] parent) =>
            {
                rw.Write(parent);
                rw.WriteSequence(static (ref RlpWriter aw) =>
                {
                    aw.WriteSequence(static (ref RlpWriter accW) =>
                    {
                        accW.Write(new byte[20]);
                        accW.WriteEncoded([0x00]); // 0x00 instead of canonical 0x80
                        accW.Write(0UL);
                        accW.Write(0UL);
                        accW.Write(ReadOnlySpan<byte>.Empty);
                        accW.WriteSequence(static (ref RlpWriter _) => { });
                    });
                });
                rw.WriteSequence(static (ref RlpWriter _) => { });
            });
        });
        Assert.Throws<RlpReaderException>(() => PreBlockAccessListDecoder.Decode(rawInvalidBool));

        // 10. Non-canonical scalar leading zero (0x82 0x00 0x01 for nonce)
        byte[] rawLeadingZeroNonce = RlpCodec.Write(validParentRlp, static (ref RlpWriter w, byte[] p) =>
        {
            w.WriteSequence(p, static (ref RlpWriter rw, byte[] parent) =>
            {
                rw.Write(parent);
                rw.WriteSequence(static (ref RlpWriter aw) =>
                {
                    aw.WriteSequence(static (ref RlpWriter accW) =>
                    {
                        accW.Write(new byte[20]);
                        accW.Write(1UL);
                        accW.WriteEncoded([0x82, 0x00, 0x01]); // leading zero in scalar
                        accW.Write(1UL);
                        accW.Write(ReadOnlySpan<byte>.Empty);
                        accW.WriteSequence(static (ref RlpWriter _) => { });
                    });
                });
                rw.WriteSequence(static (ref RlpWriter _) => { });
            });
        });
        Assert.Throws<RlpReaderException>(() => PreBlockAccessListDecoder.Decode(rawLeadingZeroNonce));
    }

    [Test]
    public async Task PreBal_EngineNewPayload_rejects_produce_pre_bal_and_malformed_pre_bal_with_InvalidParams()
    {
        using MergeTestBlockchain producer = await CreateBlockchain(Cancun.Instance);
        ExecutionPayloadV3 payload = await BuildEmptyCancunPayload(producer);

        using MergeTestBlockchain consumer = await CreateFreshConsumerBlockchain(Cancun.Instance);
        IEngineRpcModule rpc = consumer.EngineRpcModule;

        // 1. produce_pre_bal = true -> -32602
        ResultWrapper<PayloadStatusV1> prodTrue = await rpc.engine_newPayloadV3(payload, [], TestItem.KeccakE, produce_pre_bal: true, pre_bal: null);
        Assert.That(prodTrue.ErrorCode, Is.EqualTo(ErrorCodes.InvalidParams));

        // 2. produce_pre_bal = true AND pre_bal != null -> -32602
        ResultWrapper<PayloadStatusV1> bothSet = await rpc.engine_newPayloadV3(payload, [], TestItem.KeccakE, produce_pre_bal: true, pre_bal: [0xc0]);
        Assert.That(bothSet.ErrorCode, Is.EqualTo(ErrorCodes.InvalidParams));

        // 3. Invalid RLP bytes -> -32602
        ResultWrapper<PayloadStatusV1> badRlp = await rpc.engine_newPayloadV3(payload, [], TestItem.KeccakE, produce_pre_bal: false, pre_bal: [0x01, 0x02, 0x03]);
        Assert.That(badRlp.ErrorCode, Is.EqualTo(ErrorCodes.InvalidParams));

        // 4. ParentHeader hash mismatch -> -32602
        BlockHeader wrongParent = Build.A.BlockHeader.WithNumber(99).WithDifficulty(0).TestObject;
        byte[] wrongParentPreBal = PreBlockAccessListDecoder.Encode(new PreBlockAccessList(Rlp.Encode(wrongParent), [], []));
        ResultWrapper<PayloadStatusV1> mismatchParent = await rpc.engine_newPayloadV3(payload, [], TestItem.KeccakE, produce_pre_bal: false, pre_bal: wrongParentPreBal);
        Assert.That(mismatchParent.ErrorCode, Is.EqualTo(ErrorCodes.InvalidParams));

        // 5. Malformed hex string via serialized JSON-RPC (odd length & missing 0x prefix) -> -32602
        string payloadJson = consumer.JsonSerializer.Serialize(payload);
        string hashesJson = consumer.JsonSerializer.Serialize(Array.Empty<Hash256>());
        string rootStr = TestItem.KeccakE.ToString();

        string oddHexResp = await RpcTest.TestSerializedRequest(rpc, "engine_newPayloadV3", payloadJson, hashesJson, rootStr, false, "0xabc");
        using JsonDocument oddDoc = JsonDocument.Parse(oddHexResp);
        Assert.That(oddDoc.RootElement.GetProperty("error").GetProperty("code").GetInt32(), Is.EqualTo(ErrorCodes.InvalidParams));

        string noPrefixResp = await RpcTest.TestSerializedRequest(rpc, "engine_newPayloadV3", payloadJson, hashesJson, rootStr, false, "abcd");
        using JsonDocument noPrefixDoc = JsonDocument.Parse(noPrefixResp);
        Assert.That(noPrefixDoc.RootElement.GetProperty("error").GetProperty("code").GetInt32(), Is.EqualTo(ErrorCodes.InvalidParams));
    }

    [Test]
    public async Task PreBal_EngineNewPayloadV3_Cancun_JsonRpc_omitted_null_and_pre_bal_on_state_free_consumer()
    {
        using MergeTestBlockchain producer = await CreateBlockchain(Cancun.Instance);
        // Advance producer by 1 block so parentHeader (block 1) does NOT exist at all on the fresh consumer!
        await AdvanceChainByEmptyBlockV3(producer);
        BlockHeader parentHeader = producer.BlockTree.Head!.Header;

        Transaction tx = Build.A.Transaction
            .WithType(TxType.EIP1559)
            .WithTo(TestItem.AddressD)
            .WithValue(1.Ether)
            .WithGasLimit(21_000)
            .WithMaxFeePerGas(20.GWei)
            .WithMaxPriorityFeePerGas(1.GWei)
            .WithNonce(0)
            .SignedAndResolved(TestItem.PrivateKeyA)
            .TestObject;
        Assert.That(producer.TxPool.SubmitTx(tx, TxHandlingOptions.None), Is.EqualTo(AcceptTxResult.Accepted));

        ExecutionPayloadV3 payload = await BuildCancunPayload(producer, minTxs: 1, new Withdrawal
        {
            Index = 1,
            ValidatorIndex = 2,
            Address = TestItem.AddressF,
            AmountInGwei = 500_000
        });

        Block block = payload.TryGetBlock().Data!;
        PreBlockAccessList preBal = CapturePreBalFromParentState(producer, block, parentHeader);
        byte[] preBalBytes = PreBlockAccessListDecoder.Encode(preBal);

        // Fresh consumer has ONLY genesis (block 0); parentHeader (block 1) and its state are completely absent!
        using MergeTestBlockchain consumer = await CreateFreshConsumerBlockchain(Cancun.Instance);
        Assert.That(consumer.BlockTree.FindHeader(payload.ParentHash!), Is.Null);

        string payloadJson = consumer.JsonSerializer.Serialize(payload);
        string hashesJson = consumer.JsonSerializer.Serialize(Array.Empty<Hash256>());
        string beaconRootStr = payload.ParentBeaconBlockRoot!.ToString();

        // 1. Omitted trailing params -> normal stateful path -> SYNCING because parent is unknown
        string omittedResp = await RpcTest.TestSerializedRequest(consumer.EngineRpcModule, "engine_newPayloadV3", payloadJson, hashesJson, beaconRootStr);
        using (JsonDocument doc = JsonDocument.Parse(omittedResp))
        {
            Assert.That(doc.RootElement.GetProperty("result").GetProperty("status").GetString(), Is.EqualTo(PayloadStatus.Syncing));
        }

        // 2. Explicit null trailing params -> normal stateful path -> SYNCING
        string nullResp = await RpcTest.TestSerializedRequest(consumer.EngineRpcModule, "engine_newPayloadV3", payloadJson, hashesJson, beaconRootStr, null, null);
        using (JsonDocument doc = JsonDocument.Parse(nullResp))
        {
            Assert.That(doc.RootElement.GetProperty("result").GetProperty("status").GetString(), Is.EqualTo(PayloadStatus.Syncing));
        }

        // Fresh consumer #2 to verify zero BlockTree insertion from pure Pre-BAL consumer mode
        using MergeTestBlockchain statelessConsumer = await CreateFreshConsumerBlockchain(Cancun.Instance);
        Assert.That(statelessConsumer.BlockTree.FindHeader(payload.ParentHash!), Is.Null);

        string preBalHex = preBalBytes.ToHexString(withZeroX: true);
        string consumerResp = await RpcTest.TestSerializedRequest(
            statelessConsumer.EngineRpcModule,
            "engine_newPayloadV3",
            payloadJson,
            hashesJson,
            beaconRootStr,
            false,
            preBalHex);

        using (JsonDocument doc = JsonDocument.Parse(consumerResp))
        {
            JsonElement resultElem = doc.RootElement.GetProperty("result");
            using (Assert.EnterMultipleScope())
            {
                Assert.That(resultElem.GetProperty("status").GetString(), Is.EqualTo(PayloadStatus.Valid));
                Assert.That(resultElem.GetProperty("latestValidHash").GetString(), Is.EqualTo(payload.BlockHash.ToString()));
                Assert.That(resultElem.GetProperty("validationError").ValueKind, Is.EqualTo(JsonValueKind.Null));
                Assert.That(resultElem.TryGetProperty("pre_bal", out _), Is.False, "Consumer response must omit pre_bal when null");
            }
        }

        // Zero persistence check: neither parent nor block was added to BlockTree!
        using (Assert.EnterMultipleScope())
        {
            Assert.That(statelessConsumer.BlockTree.FindBlock(payload.BlockHash), Is.Null);
            Assert.That(statelessConsumer.BlockTree.FindHeader(payload.BlockHash), Is.Null);
            Assert.That(statelessConsumer.BlockTree.FindHeader(payload.ParentHash!), Is.Null);
            Assert.That(statelessConsumer.BlockTree.Head!.Number, Is.Zero);
        }
    }

    [TestCase(false, TestName = "PreBal_Consumer_multi_tx_complex_block_and_exact_match(Prague_V4)")]
    [TestCase(true, TestName = "PreBal_Consumer_multi_tx_complex_block_and_exact_match(Amsterdam_V5)")]
    public async Task PreBal_Consumer_multi_tx_complex_block_and_exact_match(bool isAmsterdam)
    {
        IReleaseSpec spec = isAmsterdam ? Amsterdam.Instance : Prague.Instance;
        using MergeTestBlockchain producer = await CreateBlockchain(spec);

        // Step 1: Deploy two contracts in Block 1 on Producer:
        //   - Contract 1 (storageContract): SLOAD(0), SSTORE(0, SLOAD(0) + 1), SLOAD(1), SSTORE(2, 0) [no-op SSTORE]
        //   - Contract 2 (blockhashContract): executes BLOCKHASH(block.number - 1), BLOCKHASH(0),
        //     BLOCKHASH(block.number) [out-of-range >= current], and SSTORE(0, xor)
        byte[] storageRuntime = Prepare.EvmCode
            .PushData(0).Op(Instruction.SLOAD)
            .PushData(1).Op(Instruction.ADD)
            .PushData(0).Op(Instruction.SSTORE)
            .PushData(1).Op(Instruction.SLOAD).Op(Instruction.POP)
            .PushData(0).PushData(2).Op(Instruction.SSTORE) // no-op write 0 -> 0 on slot 2
            .Op(Instruction.STOP)
            .Done;
        byte[] storageInit = Prepare.EvmCode
            .PushData(100).PushData(0).Op(Instruction.SSTORE) // pre-populate slot 0 = 100
            .PushData(777).PushData(1).Op(Instruction.SSTORE) // pre-populate slot 1 = 777
            .ForInitOf(storageRuntime)
            .Done;

        byte[] blockhashRuntime = Prepare.EvmCode
            .Op(Instruction.NUMBER).Op(Instruction.BLOCKHASH).Op(Instruction.POP) // out-of-range (>= current): NOT recorded!
            .PushData(0).Op(Instruction.BLOCKHASH)                                 // in-range genesis hash
            .PushData(1).Op(Instruction.NUMBER).Op(Instruction.SUB).Op(Instruction.BLOCKHASH) // in-range parent hash
            .Op(Instruction.XOR)
            .PushData(0).Op(Instruction.SSTORE)
            .Op(Instruction.STOP)
            .Done;
        byte[] blockhashInit = Prepare.EvmCode.ForInitOf(blockhashRuntime).Done;

        // Also deploy canonical EIP-4788 & EIP-2935 contracts or send ETH to them? Wait: TestBlockchain genesis/state
        // let's deploy storageContract (nonce 0 of KeyA) and blockhashContract (nonce 1 of KeyA) in Block 1!
        Address storageContract = ContractAddress.From(TestItem.AddressA, 0);
        Address blockhashContract = ContractAddress.From(TestItem.AddressA, 1);

        Transaction deployStorageTx = BuildTx(TestItem.PrivateKeyA, to: null, nonce: 0, gasLimit: 1_500_000, data: storageInit, value: 0);
        Transaction deployBlockhashTx = BuildTx(TestItem.PrivateKeyA, to: null, nonce: 1, gasLimit: 1_500_000, data: blockhashInit, value: 0);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(producer.TxPool.SubmitTx(deployStorageTx, TxHandlingOptions.None), Is.EqualTo(AcceptTxResult.Accepted));
            Assert.That(producer.TxPool.SubmitTx(deployBlockhashTx, TxHandlingOptions.None), Is.EqualTo(AcceptTxResult.Accepted));
        }

        await BuildAcceptAndCanonicalizeBlock(producer, isAmsterdam, minTxs: 2);
        BlockHeader parentHeader = producer.BlockTree.Head!.Header;
        Assert.That(parentHeader.Number, Is.EqualTo(1));

        // Step 2: Build Block 2 on Producer with 4 diverse transactions + withdrawals + beaconRoot + executionRequests:
        //   Tx 1: ETH transfer to brand-new empty account (0x...deadbeef) + EIP-2930 accessList with UNTOUCHED address & slot!
        //         (Verifies EIP-2930 WarmUp does NOT trigger MissingPreBalStateException or require unused entries!)
        Address emptyRecipient = new("0x00000000000000000000000000000000deadbeef");
        Address untouchedWarmAddr = new("0x00000000000000000000000000000000cafebabe");
        AccessList tx1AccessList = new AccessList.Builder()
            .AddAddress(untouchedWarmAddr)
            .AddStorage(new UInt256(42))
            .Build();
        Transaction tx1 = Build.A.Transaction
            .WithType(TxType.EIP1559)
            .WithTo(emptyRecipient)
            .WithValue(2.Ether)
            .WithGasLimit(40_000)
            .WithMaxFeePerGas(20.GWei)
            .WithMaxPriorityFeePerGas(2.GWei)
            .WithNonce(2)
            .WithAccessList(tx1AccessList)
            .SignedAndResolved(TestItem.PrivateKeyA)
            .TestObject;

        //   Tx 2: Contract CALL to storageContract (SLOAD + SSTORE + no-op SSTORE)
        Transaction tx2 = BuildTx(TestItem.PrivateKeyA, to: storageContract, nonce: 3, gasLimit: 500_000, data: [], value: 0);

        //   Tx 3: CREATE deploying a new contract that executes SSTORE(5, 99) in its constructor
        byte[] createInitCode = Prepare.EvmCode
            .PushData(99).PushData(5).Op(Instruction.SSTORE)
            .ForInitOf([(byte)Instruction.STOP])
            .Done;
        Transaction tx3 = BuildTx(TestItem.PrivateKeyB, to: null, nonce: 0, gasLimit: 1_500_000, data: createInitCode, value: 0);

        //   Tx 4: Contract CALL to blockhashContract executing BLOCKHASH(0), BLOCKHASH(1), and out-of-range BLOCKHASH(2)
        Transaction tx4 = BuildTx(TestItem.PrivateKeyB, to: blockhashContract, nonce: 1, gasLimit: 500_000, data: [], value: 0);

        foreach (Transaction tx in new[] { tx1, tx2, tx3, tx4 })
        {
            Assert.That(producer.TxPool.SubmitTx(tx, TxHandlingOptions.None), Is.EqualTo(AcceptTxResult.Accepted));
        }

        Withdrawal[] withdrawals =
        [
            new Withdrawal { Index = 10, ValidatorIndex = 1, Address = TestItem.AddressF, AmountInGwei = 1_000_000 },
            new Withdrawal { Index = 11, ValidatorIndex = 2, Address = new Address("0x0000000000000000000000000000000000007777"), AmountInGwei = 0 },
        ];

        (ExecutionPayload payloadObj, byte[][]? executionRequests) = await BuildPayloadForFork(producer, isAmsterdam, minTxs: 4, withdrawals);
        Block block = payloadObj.TryGetBlock().Data!;
        PreBlockAccessList preBal = CapturePreBalFromParentState(producer, block, parentHeader);
        byte[] validPreBalBytes = PreBlockAccessListDecoder.Encode(preBal);

        // Verify untouched EIP-2930 warm address is NOT in preBal, storageContract has all 3 accessed slots, and block_hashes has [0, 1] (and NOT 2)!
        Assert.That(preBal.TryGetAccount(storageContract, out PreAccountState? storageContractPreState), Is.True);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(preBal.ContainsAccount(untouchedWarmAddr), Is.False);
            Assert.That(storageContractPreState!.Storage, Has.Length.EqualTo(3));
            Assert.That(preBal.BlockHashes.Select(static bh => bh.BlockNumber), Is.EqualTo(new ulong[] { 0UL, 1UL }));
        }

        // Step 3: Submit to a completely fresh, state-free Consumer node (only Genesis block 0 in DB; no Block 1 or contracts!)
        using MergeTestBlockchain consumer = await CreateFreshConsumerBlockchain(spec);
        Assert.That(consumer.BlockTree.FindHeader(parentHeader.Hash!), Is.Null);

        ResultWrapper<PayloadStatusV1> validStatus = await SubmitPreBalPayload(consumer.EngineRpcModule, isAmsterdam, payloadObj, executionRequests, validPreBalBytes);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(validStatus.Result.ResultType, Is.EqualTo(ResultType.Success));
            Assert.That(validStatus.Data.Status, Is.EqualTo(PayloadStatus.Valid), validStatus.Data.ValidationError);
            Assert.That(validStatus.Data.LatestValidHash, Is.EqualTo(payloadObj.BlockHash));
            Assert.That(validStatus.Data.PreBal, Is.Null);
            Assert.That(consumer.BlockTree.FindBlock(payloadObj.BlockHash), Is.Null);
            Assert.That(consumer.BlockTree.FindHeader(payloadObj.BlockHash), Is.Null);
            Assert.That(consumer.BlockTree.Head!.Number, Is.Zero);
        }

        // Also test serialized JSON-RPC V4 / V5 roundtrip on the state-free consumer!
        string serializedResp = await SubmitSerializedPreBalPayload(consumer, isAmsterdam, payloadObj, executionRequests, validPreBalBytes);
        using (JsonDocument doc = JsonDocument.Parse(serializedResp))
        {
            Assert.That(doc.RootElement.GetProperty("result").GetProperty("status").GetString(), Is.EqualTo(PayloadStatus.Valid));
        }

        // Step 4: Missing Account -> INVALID with LatestValidHash == parentHeader.Hash
        PreAccountState[] missingAccountList = preBal.Accounts.Where(a => a.Address != emptyRecipient).ToArray();
        byte[] missingAccountBytes = PreBlockAccessListDecoder.Encode(new PreBlockAccessList(preBal.ParentHeader, missingAccountList, preBal.BlockHashes));
        ResultWrapper<PayloadStatusV1> missingAccRes = await SubmitPreBalPayload(consumer.EngineRpcModule, isAmsterdam, payloadObj, executionRequests, missingAccountBytes);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(missingAccRes.Data.Status, Is.EqualTo(PayloadStatus.Invalid));
            Assert.That(missingAccRes.Data.LatestValidHash, Is.EqualTo(parentHeader.Hash));
        }

        // Step 5: Missing Storage Slot -> INVALID with LatestValidHash == parentHeader.Hash
        PreAccountState[] missingSlotList = preBal.Accounts.Select(a =>
            a.Address == storageContract
                ? new PreAccountState(a.Address, a.Exists, a.Nonce, a.Balance, a.Code, a.Storage.Take(1).ToArray())
                : a).ToArray();
        byte[] missingSlotBytes = PreBlockAccessListDecoder.Encode(new PreBlockAccessList(preBal.ParentHeader, missingSlotList, preBal.BlockHashes));
        ResultWrapper<PayloadStatusV1> missingSlotRes = await SubmitPreBalPayload(consumer.EngineRpcModule, isAmsterdam, payloadObj, executionRequests, missingSlotBytes);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(missingSlotRes.Data.Status, Is.EqualTo(PayloadStatus.Invalid));
            Assert.That(missingSlotRes.Data.LatestValidHash, Is.EqualTo(parentHeader.Hash));
        }

        // Step 6: Missing BlockHash -> INVALID with LatestValidHash == parentHeader.Hash
        byte[] missingBlockHashBytes = PreBlockAccessListDecoder.Encode(new PreBlockAccessList(preBal.ParentHeader, preBal.Accounts, [preBal.BlockHashes[0]]));
        ResultWrapper<PayloadStatusV1> missingBhRes = await SubmitPreBalPayload(consumer.EngineRpcModule, isAmsterdam, payloadObj, executionRequests, missingBlockHashBytes);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(missingBhRes.Data.Status, Is.EqualTo(PayloadStatus.Invalid));
            Assert.That(missingBhRes.Data.LatestValidHash, Is.EqualTo(parentHeader.Hash));
        }

        // Step 7: Extra Unused Account -> INVALID with LatestValidHash == parentHeader.Hash
        PreAccountState extraAcc = new(untouchedWarmAddr, exists: true, nonce: 1, balance: UInt256.Zero, code: [], storage: []);
        PreAccountState[] extraAccountList = preBal.Accounts.Append(extraAcc)
            .OrderBy(static a => a.Address, Comparer<Address>.Create(static (x, y) => x!.Bytes.SequenceCompareTo(y!.Bytes)))
            .ToArray();
        byte[] extraAccountBytes = PreBlockAccessListDecoder.Encode(new PreBlockAccessList(preBal.ParentHeader, extraAccountList, preBal.BlockHashes));
        ResultWrapper<PayloadStatusV1> extraAccRes = await SubmitPreBalPayload(consumer.EngineRpcModule, isAmsterdam, payloadObj, executionRequests, extraAccountBytes);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(extraAccRes.Data.Status, Is.EqualTo(PayloadStatus.Invalid));
            Assert.That(extraAccRes.Data.LatestValidHash, Is.EqualTo(parentHeader.Hash));
            Assert.That(extraAccRes.Data.ValidationError, Does.Contain("Unused account"));
        }

        // Step 8: Extra Unused Storage Slot -> INVALID with LatestValidHash == parentHeader.Hash
        PreAccountState[] extraSlotList = preBal.Accounts.Select(a =>
            a.Address == storageContract
                ? new PreAccountState(a.Address, a.Exists, a.Nonce, a.Balance, a.Code,
                    a.Storage.Append(new PreStorageSlot(new UInt256(9999), UInt256.Zero)).OrderBy(static s => s.Key).ToArray())
                : a).ToArray();
        byte[] extraSlotBytes = PreBlockAccessListDecoder.Encode(new PreBlockAccessList(preBal.ParentHeader, extraSlotList, preBal.BlockHashes));
        ResultWrapper<PayloadStatusV1> extraSlotRes = await SubmitPreBalPayload(consumer.EngineRpcModule, isAmsterdam, payloadObj, executionRequests, extraSlotBytes);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(extraSlotRes.Data.Status, Is.EqualTo(PayloadStatus.Invalid));
            Assert.That(extraSlotRes.Data.LatestValidHash, Is.EqualTo(parentHeader.Hash));
            Assert.That(extraSlotRes.Data.ValidationError, Does.Contain("Unused storage slot"));
        }

        // Step 9: Extra Unused BlockHash -> INVALID with LatestValidHash == parentHeader.Hash
        PreBlockHash[] extraHashes = [.. preBal.BlockHashes, new PreBlockHash(2, TestItem.KeccakH)];
        byte[] extraBhBytes = PreBlockAccessListDecoder.Encode(new PreBlockAccessList(preBal.ParentHeader, preBal.Accounts, extraHashes));
        ResultWrapper<PayloadStatusV1> extraBhRes = await SubmitPreBalPayload(consumer.EngineRpcModule, isAmsterdam, payloadObj, executionRequests, extraBhBytes);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(extraBhRes.Data.Status, Is.EqualTo(PayloadStatus.Invalid));
            Assert.That(extraBhRes.Data.LatestValidHash, Is.EqualTo(parentHeader.Hash));
            Assert.That(extraBhRes.Data.ValidationError, Does.Contain("Unused block hash"));
        }
    }

    [Test]
    public async Task PreBal_Consumer_detects_blockHash_and_output_mismatches()
    {
        // Test V1 INVALID_BLOCK_HASH vs V3+ INVALID on blockHash mismatch, and GasUsed mismatch -> INVALID (latestValidHash = parentHash)
        using MergeTestBlockchain parisProducer = await CreateBlockchain(Paris.Instance);
        ExecutionPayload v1Payload = await BuildAndGetPayloadResult(parisProducer, parisProducer.EngineRpcModule);
        Block v1Block = v1Payload.TryGetBlock().Data!;
        BlockHeader parisGenesis = parisProducer.BlockTree.Genesis!;
        byte[] v1PreBal = PreBlockAccessListDecoder.Encode(CapturePreBalFromParentState(parisProducer, v1Block, parisGenesis));

        using MergeTestBlockchain parisConsumer = await CreateFreshConsumerBlockchain(Paris.Instance);
        v1Payload.BlockHash = TestItem.KeccakH; // Corrupt blockHash on V1
        ResultWrapper<PayloadStatusV1> v1BadHash = await parisConsumer.EngineRpcModule.engine_newPayloadV1(v1Payload, produce_pre_bal: false, pre_bal: v1PreBal);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(v1BadHash.Data.Status, Is.EqualTo(PayloadStatus.InvalidBlockHash));
            Assert.That(v1BadHash.Data.LatestValidHash, Is.Null);
        }

        // Test V3 blockHash mismatch -> INVALID with null LatestValidHash, and tampered GasUsed (with recomputed blockHash) -> INVALID with parentHash
        using MergeTestBlockchain cancunProducer = await CreateBlockchain(Cancun.Instance);
        await AdvanceChainByEmptyBlockV3(cancunProducer);
        BlockHeader parentHeader = cancunProducer.BlockTree.Head!.Header;
        ExecutionPayloadV3 v3Payload = await BuildCancunPayload(cancunProducer, minTxs: 0);
        Block v3Block = v3Payload.TryGetBlock().Data!;
        byte[] v3PreBal = PreBlockAccessListDecoder.Encode(CapturePreBalFromParentState(cancunProducer, v3Block, parentHeader));

        using MergeTestBlockchain cancunConsumer = await CreateFreshConsumerBlockchain(Cancun.Instance);
        v3Payload.BlockHash = TestItem.KeccakG;
        ResultWrapper<PayloadStatusV1> v3BadHash = await cancunConsumer.EngineRpcModule.engine_newPayloadV3(v3Payload, [], v3Payload.ParentBeaconBlockRoot, false, v3PreBal);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(v3BadHash.Data.Status, Is.EqualTo(PayloadStatus.Invalid));
            Assert.That(v3BadHash.Data.LatestValidHash, Is.Null);
        }

        // Restore valid structure with tampered GasUsed and recomputed BlockHash -> fails output verification with LatestValidHash = parentHeader.Hash!
        v3Block.Header.GasUsed = 21_000;
        v3Block.Header.Hash = v3Block.Header.CalculateHash();
        ExecutionPayloadV3 tamperedGasPayload = ExecutionPayloadV3.Create(v3Block);
        ResultWrapper<PayloadStatusV1> tamperedGasRes = await cancunConsumer.EngineRpcModule.engine_newPayloadV3(
            tamperedGasPayload, [], tamperedGasPayload.ParentBeaconBlockRoot, false, v3PreBal);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(tamperedGasRes.Data.Status, Is.EqualTo(PayloadStatus.Invalid));
            Assert.That(tamperedGasRes.Data.LatestValidHash, Is.EqualTo(parentHeader.Hash));
        }
    }

    [Test]
    public async Task PreBal_EngineNewPayloadV6_Bogota_stateless_consumer_validates_and_preserves_zero_persistence()
    {
        using MergeTestBlockchain bogotaProducer = await CreateBlockchain(Bogota.Instance);
        BlockHeader genesis = bogotaProducer.BlockTree.Head!.Header;
        PayloadAttributes attrs = new()
        {
            Timestamp = genesis.Timestamp + 12,
            PrevRandao = TestItem.KeccakH,
            SuggestedFeeRecipient = TestItem.AddressE,
            Withdrawals = [],
            ParentBeaconBlockRoot = TestItem.KeccakE,
            SlotNumber = 1,
            TargetGasLimit = genesis.GasLimit,
            InclusionListTransactions = [],
        };
        ForkchoiceStateV1 fcu = new(genesis.Hash!, genesis.Hash!, genesis.Hash!);
        ResultWrapper<ForkchoiceUpdatedV2Result> fcuRes = await bogotaProducer.EngineRpcModule.engine_forkchoiceUpdatedV5(fcu, attrs);
        Assert.That(fcuRes.Result.ResultType, Is.EqualTo(ResultType.Success));

        ResultWrapper<GetPayloadV6Result?> payloadRes = await bogotaProducer.EngineRpcModule.engine_getPayloadV6(Bytes.FromHexString(fcuRes.Data.PayloadId!));
        ExecutionPayloadV4 v6Payload = payloadRes.Data!.ExecutionPayload;
        v6Payload.ParentBeaconBlockRoot = attrs.ParentBeaconBlockRoot;
        Block v6Block = v6Payload.TryGetBlock().Data!;
        byte[] preBalBytes = PreBlockAccessListDecoder.Encode(CapturePreBalFromParentState(bogotaProducer, v6Block, genesis));

        using MergeTestBlockchain bogotaConsumer = await CreateFreshConsumerBlockchain(Bogota.Instance);
        ResultWrapper<PayloadStatusV2> v6Status = await bogotaConsumer.EngineRpcModule.engine_newPayloadV6(
            v6Payload, [], v6Payload.ParentBeaconBlockRoot, payloadRes.Data.ExecutionRequests, [], produce_pre_bal: false, pre_bal: preBalBytes);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(v6Status.Result.ResultType, Is.EqualTo(ResultType.Success));
            Assert.That(v6Status.Data.Status, Is.EqualTo(PayloadStatus.Valid), v6Status.Data.ValidationError);
            Assert.That(v6Status.Data.LatestValidHash, Is.EqualTo(v6Payload.BlockHash));
            Assert.That(v6Status.Data.InclusionListSatisfied, Is.True);
            Assert.That(v6Status.Data.PreBal, Is.Null);
            Assert.That(bogotaConsumer.BlockTree.FindBlock(v6Payload.BlockHash), Is.Null);
        }
    }

    [Test]
    public async Task PreBal_Besu_produced_Prague_payload_and_pre_bal_cross_client_verification()
    {
        const string besuPayloadJson = """
            {
              "parentHash" : "0x56e867cc0cd639d6b583cfd066b4a6864524ef309566197932490e3fb656825a",
              "feeRecipient" : "0x000000000000000000000000000000000000feee",
              "stateRoot" : "0x01c1ceeeb08a9640bd7b620e95745fac3289fdb719ce68cb010554b456db1261",
              "receiptsRoot" : "0xb803b16c01ca8617148cede9ffc7503d78f32d350a978e2131f010092f845d06",
              "logsBloom" : "0x00000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000002000000000000000000000000000000000000000000000000000000200000000000000000000000000000000000000000000000000000000000000000000000000100000000000800000000400000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000040000000000000000000000000000000000000000000000000000000000000000000000",
              "prevRandao" : "0xbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb",
              "blockNumber" : "0x2",
              "gasLimit" : "0x1c9c380",
              "gasUsed" : "0x16f9b",
              "timestamp" : "0x18",
              "extraData" : "0x",
              "baseFeePerGas" : "0x2da282a8",
              "blockHash" : "0x3e18feb8a184a98f19be843d6f3fe128d1834be58ec3f61d58e0953f5d51b80a",
              "transactions" : [ "0x02f870820539058477359400847735940082520894000000000000000000000000000000000000beef84499602d280c080a0d7168a805621ab80a29ddb6136026d330a608c68f4c66470463325e61fddfb95a01f15a4c92925a53c009d6dd109aeff21f12e099ab94d02a09156317173ada769", "0x02f86d820539068477359400847735940083030d4094000000000000000000000000000000000000c0de8080c080a0634c91b98aebc20c8a7a8f57cf8efa12e3a339ab1d32bec0d7d97dae9fa4a682a01a62fc3ca846943e97d09ceb4d42a58e1d08d7f3ed2e9b3343359b6dd6f2bc98" ],
              "withdrawals" : [ {
                "index" : "0x0",
                "validatorIndex" : "0x7",
                "address" : "0x000000000000000000000000000000000000cafe",
                "amount" : "0x1388"
              } ],
              "blobGasUsed" : "0x0",
              "excessBlobGas" : "0x0"
            }
            """;

        const string besuPreBalHex = "0xf9082db9025ef9025ba0eb50a41fb09f42fbd6acac159a4339d1b37a5243a46d316ba6960f534762c849a01dcc4de8dec75d7aab85b567b6ccd41ad312451b948a7413f0a142fd40d4934794000000000000000000000000000000000000feeea0d84d070dbb7df8411fc3de192057b2709b565dacd9e93d34e4eb82165e156b18a056e81f171bcc55a6ff8345e692c0f86e5b48e01b996cadc001622fb5e363b421a056e81f171bcc55a6ff8345e692c0f86e5b48e01b996cadc001622fb5e363b421b901000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000080018401c9c380800c80a0aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa88000000000000000084342770c0a056e81f171bcc55a6ff8345e692c0f86e5b48e01b996cadc001622fb5e363b4218080a01111111111111111111111111111111111111111111111111111111111111111a0e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855f905a5da94000000000000000000000000000000000000beef80808080c0f84994000000000000000000000000000000000000c0de01018203e8a16000545060015450609960015561beef60025560004060035560aa60006000a100ccc2802ac20107c20280c20380da94000000000000000000000000000000000000cafe80808080c0da94000000000000000000000000000000000000feee80808080c0f902209400000961ef480eb55e80d19ad83579a64c007002010180b901f83373fffffffffffffffffffffffffffffffffffffffe1460cb5760115f54807fffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff146101f457600182026001905f5b5f82111560685781019083028483029004916001019190604d565b909390049250505036603814608857366101f457346101f4575f5260205ff35b34106101f457600154600101600155600354806003026004013381556001015f35815560010160203590553360601b5f5260385f601437604c5fa0600101600355005b6003546002548082038060101160df575060105b5f5b8181146101835782810160030260040181604c02815460601b8152601401816001015481526020019060020154807fffffffffffffffffffffffffffffffff00000000000000000000000000000000168252906010019060401c908160381c81600701538160301c81600601538160281c81600501538160201c81600401538160181c81600301538160101c81600201538160081c81600101535360010160e1565b910180921461019557906002556101a0565b90505f6002555f6003555b5f54807fffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff14156101cd57505f5b6001546002828201116101e25750505f6101e8565b01600290035b5f555f600155604c025ff35b5f5ffdccc28080c20180c20280c20380f901c6940000bbddc7ce488642fb579f8b00f3a590007251010180b9019e3373fffffffffffffffffffffffffffffffffffffffe1460d35760115f54807fffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff1461019a57600182026001905f5b5f82111560685781019083028483029004916001019190604d565b9093900492505050366060146088573661019a573461019a575f5260205ff35b341061019a57600154600101600155600354806004026004013381556001015f358155600101602035815560010160403590553360601b5f5260605f60143760745fa0600101600355005b6003546002548082038060021160e7575060025b5f5b8181146101295782810160040260040181607402815460601b815260140181600101548152602001816002015481526020019060030154905260010160e9565b910180921461013b5790600255610146565b90505f6002555f6003555b5f54807fffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff141561017357505f5b6001546001828201116101885750505f61018e565b01600190035b5f555f6001556074025ff35b5f5ffdccc28080c20180c20280c20380f871940000f90827f1c53a10cb7a02335b175320002935010180b8533373fffffffffffffffffffffffffffffffffffffffe14604657602036036042575f35600143038111604257611fff81430311604257611fff9006545f5260205ff35b5f5ffd5b5f35611fff60014303065500c3c20180f88494000f3df6d732807ef1319fb7b8bb8522d0beac02010180b8613373fffffffffffffffffffffffffffffffffffffffe14604d57602036146024575f5ffd5b5f35801560495762001fff810690815414603c575f5ffd5b62001fff01545f5260205ff35b5f5ffd5b62001fff42064281555f359062001fff015500c8c21880c482201780e394a94f5374fce5edbc8e2a8697c15331677e6ebf0b0105893635c9adc5dea0000080c0e3e280a0eb50a41fb09f42fbd6acac159a4339d1b37a5243a46d316ba6960f534762c849";
        const string beaconRoot = "0x2222222222222222222222222222222222222222222222222222222222222222";
        const string expectedBlockHash = "0x3e18feb8a184a98f19be843d6f3fe128d1834be58ec3f61d58e0953f5d51b80a";
        const string expectedParentHash = "0x56e867cc0cd639d6b583cfd066b4a6864524ef309566197932490e3fb656825a";

        SingleReleaseSpecProvider specProvider = new(Prague.Instance, 1337UL, 1337UL);
        specProvider.UpdateMergeTransitionInfo(0, UInt256.Zero);
        using MergeTestBlockchain consumer = await CreateBlockchain(specProvider);

        // 1. Exact Besu-produced pre_bal over serialized JSON-RPC engine_newPayloadV4 -> VALID!
        string validResp = await RpcTest.TestSerializedRequest(
            consumer.EngineRpcModule,
            "engine_newPayloadV4",
            besuPayloadJson,
            "[]",
            beaconRoot,
            "[]",
            false,
            besuPreBalHex);

        using (JsonDocument doc = JsonDocument.Parse(validResp))
        {
            JsonElement res = doc.RootElement.GetProperty("result");
            using (Assert.EnterMultipleScope())
            {
                Assert.That(res.GetProperty("status").GetString(), Is.EqualTo(PayloadStatus.Valid), validResp);
                Assert.That(res.GetProperty("latestValidHash").GetString(), Is.EqualTo(expectedBlockHash));
                Assert.That(res.TryGetProperty("pre_bal", out _), Is.False);
            }
        }

        // 2. Removing an accessed storage slot from Besu's pre_bal -> INVALID!
        PreBlockAccessList decoded = PreBlockAccessListDecoder.Decode(Bytes.FromHexString(besuPreBalHex));
        Address contractAddr = new("0x000000000000000000000000000000000000c0de");
        PreAccountState[] missingSlotAccounts = decoded.Accounts.Select(a =>
            a.Address == contractAddr
                ? new PreAccountState(a.Address, a.Exists, a.Nonce, a.Balance, a.Code, a.Storage.Skip(1).ToArray())
                : a).ToArray();
        string missingSlotHex = PreBlockAccessListDecoder
            .Encode(new PreBlockAccessList(decoded.ParentHeader, missingSlotAccounts, decoded.BlockHashes))
            .ToHexString(withZeroX: true);

        string missingSlotResp = await RpcTest.TestSerializedRequest(
            consumer.EngineRpcModule,
            "engine_newPayloadV4",
            besuPayloadJson,
            "[]",
            beaconRoot,
            "[]",
            false,
            missingSlotHex);
        using (JsonDocument doc = JsonDocument.Parse(missingSlotResp))
        {
            JsonElement res = doc.RootElement.GetProperty("result");
            using (Assert.EnterMultipleScope())
            {
                Assert.That(res.GetProperty("status").GetString(), Is.EqualTo(PayloadStatus.Invalid));
                Assert.That(res.GetProperty("latestValidHash").GetString(), Is.EqualTo(expectedParentHash));
            }
        }

        // 3. Adding an unqueried parent BLOCKHASH(1) to Besu's pre_bal -> INVALID!
        PreBlockHash[] extraHashes = [.. decoded.BlockHashes, new PreBlockHash(1, new Hash256(expectedParentHash))];
        string extraBhHex = PreBlockAccessListDecoder
            .Encode(new PreBlockAccessList(decoded.ParentHeader, decoded.Accounts, extraHashes))
            .ToHexString(withZeroX: true);

        string extraBhResp = await RpcTest.TestSerializedRequest(
            consumer.EngineRpcModule,
            "engine_newPayloadV4",
            besuPayloadJson,
            "[]",
            beaconRoot,
            "[]",
            false,
            extraBhHex);
        using (JsonDocument doc = JsonDocument.Parse(extraBhResp))
        {
            JsonElement res = doc.RootElement.GetProperty("result");
            using (Assert.EnterMultipleScope())
            {
                Assert.That(res.GetProperty("status").GetString(), Is.EqualTo(PayloadStatus.Invalid));
                Assert.That(res.GetProperty("latestValidHash").GetString(), Is.EqualTo(expectedParentHash));
            }
        }
    }

    private async Task<MergeTestBlockchain> CreateFreshConsumerBlockchain(IReleaseSpec spec) =>
        await CreateBlockchain(spec);

    private static Transaction BuildTx(PrivateKey sender, Address? to, ulong nonce, ulong gasLimit, byte[] data, UInt256 value) =>
        (to is null ? Build.A.Transaction.WithCode(data) : Build.A.Transaction.WithTo(to).WithData(data))
            .WithType(TxType.EIP1559)
            .WithNonce(nonce)
            .WithValue(value)
            .WithGasLimit(gasLimit)
            .WithMaxFeePerGas(20.GWei)
            .WithMaxPriorityFeePerGas(2.GWei)
            .SignedAndResolved(sender)
            .TestObject;

    private static async Task<ExecutionPayloadV3> BuildEmptyCancunPayload(MergeTestBlockchain chain) =>
        await BuildCancunPayload(chain, minTxs: 0);

    private static async Task AdvanceChainByEmptyBlockV3(MergeTestBlockchain chain)
    {
        ExecutionPayloadV3 payload = await BuildCancunPayload(chain, minTxs: 0);
        ResultWrapper<PayloadStatusV1> np = await chain.EngineRpcModule.engine_newPayloadV3(payload, [], payload.ParentBeaconBlockRoot);
        Assert.That(np.Data.Status, Is.EqualTo(PayloadStatus.Valid));
        ForkchoiceStateV1 fcu = new(payload.BlockHash, payload.BlockHash, payload.BlockHash);
        ResultWrapper<ForkchoiceUpdatedV1Result> fcuRes = await chain.EngineRpcModule.engine_forkchoiceUpdatedV3(fcu);
        Assert.That(fcuRes.Data.PayloadStatus.Status, Is.EqualTo(PayloadStatus.Valid));
        await chain.WaitForCommitted(payload.BlockHash);
    }

    private static async Task<ExecutionPayloadV3> BuildCancunPayload(MergeTestBlockchain chain, int minTxs, params Withdrawal[] withdrawals)
    {
        BlockHeader head = chain.BlockTree.Head!.Header;
        PayloadAttributes attrs = new()
        {
            Timestamp = head.Timestamp + 12,
            PrevRandao = TestItem.KeccakH,
            SuggestedFeeRecipient = TestItem.AddressE,
            Withdrawals = withdrawals,
            ParentBeaconBlockRoot = TestItem.KeccakE,
        };
        ForkchoiceStateV1 fcuState = new(head.Hash!, head.Hash!, head.Hash!);
        Task? waitImproved = minTxs > 0 ? chain.WaitForImprovedBlock(head.Hash!, minTxs) : null;
        ResultWrapper<ForkchoiceUpdatedV1Result> fcu = await chain.EngineRpcModule.engine_forkchoiceUpdatedV3(fcuState, attrs);
        Assert.That(fcu.Result.ResultType, Is.EqualTo(ResultType.Success));
        if (waitImproved is not null)
        {
            await waitImproved;
        }

        ResultWrapper<GetPayloadV3Result?> res = await chain.EngineRpcModule.engine_getPayloadV3(Bytes.FromHexString(fcu.Data.PayloadId!));
        res.Data!.ExecutionPayload.ParentBeaconBlockRoot = attrs.ParentBeaconBlockRoot;
        return res.Data.ExecutionPayload;
    }

    private static async Task BuildAcceptAndCanonicalizeBlock(MergeTestBlockchain chain, bool isAmsterdam, int minTxs)
    {
        (ExecutionPayload payload, byte[][]? requests) = await BuildPayloadForFork(chain, isAmsterdam, minTxs);
        if (isAmsterdam)
        {
            ExecutionPayloadV4 v4 = (ExecutionPayloadV4)payload;
            ResultWrapper<PayloadStatusV1> np = await chain.EngineRpcModule.engine_newPayloadV5(v4, [], v4.ParentBeaconBlockRoot, requests);
            Assert.That(np.Data.Status, Is.EqualTo(PayloadStatus.Valid), np.Data.ValidationError);
            ForkchoiceStateV1 fcu = new(v4.BlockHash, v4.BlockHash, v4.BlockHash);
            Assert.That((await chain.EngineRpcModule.engine_forkchoiceUpdatedV4(fcu)).Data.PayloadStatus.Status, Is.EqualTo(PayloadStatus.Valid));
            await chain.WaitForCommitted(v4.BlockHash);
        }
        else
        {
            ExecutionPayloadV3 v3 = (ExecutionPayloadV3)payload;
            ResultWrapper<PayloadStatusV1> np = await chain.EngineRpcModule.engine_newPayloadV4(v3, [], v3.ParentBeaconBlockRoot, requests);
            Assert.That(np.Data.Status, Is.EqualTo(PayloadStatus.Valid), np.Data.ValidationError);
            ForkchoiceStateV1 fcu = new(v3.BlockHash, v3.BlockHash, v3.BlockHash);
            Assert.That((await chain.EngineRpcModule.engine_forkchoiceUpdatedV3(fcu)).Data.PayloadStatus.Status, Is.EqualTo(PayloadStatus.Valid));
            await chain.WaitForCommitted(v3.BlockHash);
        }
    }

    private static async Task<(ExecutionPayload Payload, byte[][]? ExecutionRequests)> BuildPayloadForFork(
        MergeTestBlockchain chain,
        bool isAmsterdam,
        int minTxs,
        params Withdrawal[] withdrawals)
    {
        BlockHeader head = chain.BlockTree.Head!.Header;
        PayloadAttributes attrs = new()
        {
            Timestamp = head.Timestamp + 12,
            PrevRandao = TestItem.KeccakH,
            SuggestedFeeRecipient = TestItem.AddressE,
            Withdrawals = withdrawals,
            ParentBeaconBlockRoot = TestItem.KeccakE,
            SlotNumber = isAmsterdam ? (head.SlotNumber ?? 0) + 1 : null,
            TargetGasLimit = isAmsterdam ? head.GasLimit : null,
        };
        ForkchoiceStateV1 fcuState = new(head.Hash!, head.Hash!, head.Hash!);
        Task? waitImproved = minTxs > 0 ? chain.WaitForImprovedBlock(head.Hash!, minTxs) : null;

        if (isAmsterdam)
        {
            ResultWrapper<ForkchoiceUpdatedV1Result> fcu = await chain.EngineRpcModule.engine_forkchoiceUpdatedV4(fcuState, attrs);
            Assert.That(fcu.Result.ResultType, Is.EqualTo(ResultType.Success));
            if (waitImproved is not null) await waitImproved;
            ResultWrapper<GetPayloadV6Result?> res = await chain.EngineRpcModule.engine_getPayloadV6(Bytes.FromHexString(fcu.Data.PayloadId!));
            res.Data!.ExecutionPayload.ParentBeaconBlockRoot = attrs.ParentBeaconBlockRoot;
            return (res.Data.ExecutionPayload, res.Data.ExecutionRequests);
        }
        else
        {
            ResultWrapper<ForkchoiceUpdatedV1Result> fcu = await chain.EngineRpcModule.engine_forkchoiceUpdatedV3(fcuState, attrs);
            Assert.That(fcu.Result.ResultType, Is.EqualTo(ResultType.Success));
            if (waitImproved is not null) await waitImproved;
            ResultWrapper<GetPayloadV4Result?> res = await chain.EngineRpcModule.engine_getPayloadV4(Bytes.FromHexString(fcu.Data.PayloadId!));
            res.Data!.ExecutionPayload.ParentBeaconBlockRoot = attrs.ParentBeaconBlockRoot;
            return (res.Data.ExecutionPayload, res.Data.ExecutionRequests);
        }
    }

    private static Task<ResultWrapper<PayloadStatusV1>> SubmitPreBalPayload(
        IEngineRpcModule rpc,
        bool isAmsterdam,
        ExecutionPayload payload,
        byte[][]? executionRequests,
        byte[] preBalBytes) =>
        isAmsterdam
            ? rpc.engine_newPayloadV5((ExecutionPayloadV4)payload, [], payload.ParentBeaconBlockRoot, executionRequests, produce_pre_bal: false, pre_bal: preBalBytes)
            : rpc.engine_newPayloadV4((ExecutionPayloadV3)payload, [], payload.ParentBeaconBlockRoot, executionRequests, produce_pre_bal: false, pre_bal: preBalBytes);

    private static Task<string> SubmitSerializedPreBalPayload(
        MergeTestBlockchain chain,
        bool isAmsterdam,
        ExecutionPayload payload,
        byte[][]? executionRequests,
        byte[] preBalBytes)
    {
        string method = isAmsterdam ? "engine_newPayloadV5" : "engine_newPayloadV4";
        string payloadJson = isAmsterdam
            ? chain.JsonSerializer.Serialize((ExecutionPayloadV4)payload)
            : chain.JsonSerializer.Serialize((ExecutionPayloadV3)payload);
        string hashesJson = chain.JsonSerializer.Serialize(Array.Empty<Hash256>());
        string rootStr = payload.ParentBeaconBlockRoot!.ToString();
        string requestsJson = chain.JsonSerializer.Serialize(executionRequests ?? []);
        string preBalHex = preBalBytes.ToHexString(withZeroX: true);
        return RpcTest.TestSerializedRequest(chain.EngineRpcModule, method, payloadJson, hashesJson, rootStr, requestsJson, null, preBalHex);
    }

    /// <summary>
    /// Test helper simulating Besu Producer mode: replays <paramref name="block"/> against <paramref name="parentHeader"/>'s
    /// read-only world state with EIP-7928 access tracking + BLOCKHASH recording, and materializes a canonical <see cref="PreBlockAccessList"/>.
    /// </summary>
    private static PreBlockAccessList CapturePreBalFromParentState(
        MergeTestBlockchain producer,
        Block block,
        BlockHeader parentHeader)
    {
        ISpecProvider specProvider = producer.SpecProvider;
        IReleaseSpec spec = specProvider.GetSpec(block.Header);

        using IOverridableWorldScope overridableScope = producer.WorldStateManager.CreateOverridableWorldScope();
        WorldState baseWorldState = new(overridableScope.WorldState, LimboLogs.Instance);
        Assert.That(baseWorldState.TryBeginScope(parentHeader, out IDisposable? scopeCloser), Is.True);
        using IDisposable worldScope = scopeCloser!;

        TracedAccessWorldState tracedState = new(baseWorldState, parallel: false);
        tracedState.SetGeneratingBlockAccessList(new BlockAccessListAtIndex());

        RecordingBlockhashProvider recordingBlockhash = new(producer.BlockTree);
        CacheCodeInfoRepository codeRepo = new(tracedState, new EthereumPrecompileProvider(), new StaticCodeCache(512));
        using EthereumVirtualMachine vm = new(recordingBlockhash, specProvider, LimboLogs.Instance);
        EthereumTransactionProcessor txProc = new(BlobBaseFeeCalculator.Instance, specProvider, tracedState, vm, codeRepo, LimboLogs.Instance);

        GeneratedBlockAccessList generatedBal = new();
        ExecuteBlockRecordingBal(block, parentHeader, specProvider, spec, tracedState, txProc, generatedBal);

        // Reset overlays so we read pure pre-block parent state for every accessed account & slot!
        overridableScope.ResetOverrides();
        baseWorldState.Reset();

        using ArrayPoolListRef<GeneratedAccountChanges> sortedAccounts = generatedBal.GetSortedAccountChanges();
        List<PreAccountState> preAccounts = [with(sortedAccounts.Count)];
        foreach (GeneratedAccountChanges genAcc in sortedAccounts)
        {
            Address address = genAcc.Address;
            bool exists = baseWorldState.AccountExists(address) && !baseWorldState.IsDeadAccount(address);
            ulong nonce = exists ? baseWorldState.GetNonce(address) : 0UL;
            UInt256 balance = exists ? baseWorldState.GetBalance(address) : UInt256.Zero;
            byte[] code = exists ? baseWorldState.GetCode(address).ToArray() : [];

            SortedSet<UInt256> allKeys = [];
            foreach (UInt256 r in genAcc.StorageReads) allKeys.Add(r);
            foreach (GeneratedSlotChanges sc in genAcc.StorageChanges) allKeys.Add(sc.Key);

            PreStorageSlot[] slots = new PreStorageSlot[allKeys.Count];
            int idx = 0;
            foreach (UInt256 key in allKeys)
            {
                UInt256 val = UInt256.Zero;
                if (baseWorldState.AccountExists(address))
                {
                    baseWorldState.Get(new StorageCell(address, in key), out val);
                }
                slots[idx++] = new PreStorageSlot(key, val);
            }

            preAccounts.Add(new PreAccountState(address, exists, nonce, balance, code, slots));
        }

        PreBlockHash[] blockHashes = recordingBlockhash.AccessedHashes
            .OrderBy(static kv => kv.Key)
            .Select(static kv => new PreBlockHash(kv.Key, kv.Value))
            .ToArray();

        byte[] parentHeaderRlp = Rlp.Encode(parentHeader);
        return new PreBlockAccessList(parentHeaderRlp, [.. preAccounts], blockHashes);
    }

    private static void ExecuteBlockRecordingBal(
        Block suggestedBlock,
        BlockHeader parentHeader,
        ISpecProvider specProvider,
        IReleaseSpec spec,
        TracedAccessWorldState tracedState,
        EthereumTransactionProcessor txProc,
        GeneratedBlockAccessList targetBal)
    {
        EthereumEcdsa ecdsa = new(specProvider.ChainId);
        foreach (Transaction tx in suggestedBlock.Transactions)
        {
            tx.SenderAddress ??= ecdsa.RecoverAddress(tx, !spec.ValidateChainId);
        }

        Block block = suggestedBlock.WithReplacedHeader(suggestedBlock.Header.CloneForProcessing());
        ExecuteTransactionProcessorAdapter adapter = new(txProc);
        adapter.SetBlockExecutionContext(new BlockExecutionContext(block.Header, spec));

        tracedState.Clear();
        tracedState.SetIndex(0u);
        new ZeroNonceStorageAccountsTransition(specProvider, new StatelessBlockTree([parentHeader])).ApplyIfForkBlock(block.Header, spec, tracedState);
        new BeaconBlockRootHandler(txProc, tracedState).StoreBeaconRoot(block, spec, NullTxTracer.Instance);

        BlockhashStore bhStore = new(tracedState);
        if (!spec.IsEip8037Enabled)
        {
            if (bhStore.TryGetHistoryContract(block.Header, spec, out Address? hc))
            {
                tracedState.Get(new StorageCell(hc, new UInt256((ulong)(block.Header.Number - 1) % spec.Eip2935RingBufferSize)), out _);
            }
            bhStore.ApplyBlockhashStateChanges(block.Header, spec);
        }
        else if (bhStore.TryGetHistoryContract(block.Header, spec, out Address? hc))
        {
            txProc.Execute(new SystemCall
            {
                GasLimit = Eip8037Constants.SystemCallGasLimit,
                Data = block.Header.ParentHash!.Bytes.ToArray(),
                To = hc,
                SenderAddress = Address.SystemUser
            }, NullTxTracer.Instance);
        }

        tracedState.Commit(spec, commitRoots: false);
        targetBal.Merge(tracedState.GetGeneratingBlockAccessList()!);
        tracedState.Clear();
        tracedState.IncrementIndex();

        BlockReceiptsTracer receiptsTracer = new();
        receiptsTracer.StartNewBlockTrace(block);
        for (int i = 0; i < block.Transactions.Length; i++)
        {
            adapter.ProcessTransaction(block.Transactions[i], receiptsTracer, ProcessingOptions.ReadOnlyChain, tracedState);
            targetBal.Merge(tracedState.GetGeneratingBlockAccessList()!);
            tracedState.Clear();
            tracedState.IncrementIndex();
        }

        tracedState.Commit(spec, commitRoots: false);
        new WithdrawalProcessor(tracedState, LimboLogs.Instance).ProcessWithdrawals(block, spec);
        tracedState.Commit(spec, commitRoots: false);
        new ExecutionRequestsProcessor(txProc).ProcessExecutionRequests(block, tracedState, [.. receiptsTracer.TxReceipts], spec);
        tracedState.Commit(spec, commitRoots: false);
        targetBal.Merge(tracedState.GetGeneratingBlockAccessList()!);
    }

    private sealed class RecordingBlockhashProvider(IBlockTree blockTree) : IBlockhashProvider
    {
        public Dictionary<ulong, Hash256> AccessedHashes { get; } = [];

        public Hash256? GetBlockhash(BlockHeader currentBlock, ulong number, IReleaseSpec spec)
        {
            if (number >= currentBlock.Number || currentBlock.Number - number > 256UL)
            {
                return null;
            }

            Hash256? hash = blockTree.FindHeader(number, BlockTreeLookupOptions.TotalDifficultyNotNeeded)?.Hash;
            if (hash is not null)
            {
                AccessedHashes[number] = hash;
            }

            return hash;
        }

        public Task Prefetch(BlockHeader currentBlock, CancellationToken token) => Task.CompletedTask;
    }
}
