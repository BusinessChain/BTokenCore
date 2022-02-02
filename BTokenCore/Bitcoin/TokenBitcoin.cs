using System;
using System.Collections.Generic;
using System.Linq;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using System.Security.Cryptography;


using BTokenLib;

namespace BTokenCore
{
  class TokenBitcoin : Token
  {
    UTXOTable UTXOTable;
    Crypto Crypto = new();



    public TokenBitcoin(string pathBlockArchive)
      : base(pathBlockArchive)
    {
      Blockchain.Checkpoints.Add(
        250000, 
        "000000000000003887df1f29024b06fc2200b55f8af8f35453d7be294df2d214".ToBinary());

      UTXOTable = new(GetGenesisBlockBytes());
    }


    const int LENGTH_P2PKH = 25;
    byte[] PREFIX_OP_RETURN = new byte[] { 0x6A, 0x50 };
    byte OP_RETURN = 0x6A;
    byte[] PREFIX_P2PKH = new byte[] { 0x76, 0xA9, 0x14 };

    byte[] POSTFIX_P2PKH = new byte[] { 0x88, 0xAC };
    byte[] PublicKeyHash160 = new byte[20];

    public override TX CreateDataTX(
      byte[] dataOPReturn)
    {
      ulong fee = 10000;

      TXOutputWallet outputSpendable = Wallet.GetTXOutputWallet(fee);        

      if (outputSpendable == null)
        throw new ProtocolException("No spendable output found.");

      List<byte> tXRaw = new();

      byte[] version = { 0x01, 0x00, 0x00, 0x00 };
      tXRaw.AddRange(version);

      byte countInputs = 1;
      tXRaw.Add(countInputs);

      tXRaw.AddRange(outputSpendable.TXID);

      tXRaw.AddRange(BitConverter.GetBytes(
        outputSpendable.OutputIndex));

      int indexScriptSig = tXRaw.Count;

      tXRaw.Add(LENGTH_P2PKH);

      tXRaw.AddRange(outputSpendable.ScriptPubKey);

      byte[] sequence = { 0xFF, 0xFF, 0xFF, 0xFF };
      tXRaw.AddRange(sequence);

      byte countOutputs = 2; //(byte)(valueChange == 0 ? 1 : 2);
      tXRaw.Add(countOutputs);

      ulong valueChange = outputSpendable.Value - fee;
      tXRaw.AddRange(BitConverter.GetBytes(
        valueChange));

      tXRaw.Add(LENGTH_P2PKH);

      tXRaw.AddRange(PREFIX_P2PKH);
      tXRaw.AddRange(PublicKeyHash160);
      tXRaw.AddRange(POSTFIX_P2PKH);

      tXRaw.AddRange(BitConverter.GetBytes(
        (ulong)0));

      tXRaw.Add((byte)(dataOPReturn.Length + 2));
      tXRaw.Add(OP_RETURN);
      tXRaw.Add((byte)dataOPReturn.Length);
      tXRaw.AddRange(dataOPReturn);

      var lockTime = new byte[4];
      tXRaw.AddRange(lockTime);

      byte[] sigHashType = { 0x01, 0x00, 0x00, 0x00 };
      tXRaw.AddRange(sigHashType);

      var tXRawPreScriptSig = tXRaw.Take(indexScriptSig);
      var tXRawPostScriptSig = tXRaw.Skip(indexScriptSig + LENGTH_P2PKH + 1);

      List<byte> scriptSig = Wallet.GetScriptSignature(
        tXRaw.ToArray());

      tXRaw = tXRawPreScriptSig
        .Concat(new byte[] { (byte)scriptSig.Count })
        .Concat(scriptSig)
        .Concat(tXRawPostScriptSig)
        .ToList();

      tXRaw.RemoveRange(tXRaw.Count - 4, 4);

      var parser = new ParserBitcoin();
      int indexTXRaw = 0;
      byte[] tXRawArray = tXRaw.ToArray();

      TX tX = parser.ParseTX(
        false,
        tXRawArray,
        ref indexTXRaw);

      tX.TXRaw = tXRawArray;

      return tX;
    }



    public override void StartMining(object network)
    {
      if (IsMining)
        return;

      IsMining = true;

      int numberOfProcesses = Math.Max(Environment.ProcessorCount - 1, 1);
      long nonceSegment = uint.MaxValue / numberOfProcesses;

      Parallel.For(
        0,
        numberOfProcesses,
        i => RunMining((Network)network, i * nonceSegment));

      IsMining = false;
      FlagMiningCancel = false;

      Console.WriteLine("Miner canceled.");
    }

    protected async override Task<Block> MineBlock(
      SHA256 sHA256, 
      long nonceStart)
    {
      BlockBitcoin block = new();

      ComputePoW(
        block,
        sHA256,
        nonceStart);

      block.Buffer = block.Header.Buffer
        .Concat(VarInt.GetBytes(block.TXs.Count))
        .Concat(block.TXs[0].TXRaw).ToArray();

      block.Header.CountBlockBytes = block.Buffer.Length;

      return block;
    }

    byte[] LoadTXs(BlockBitcoin block)
    {
      List<byte> tXRaw = new();

      tXRaw.AddRange(new byte[4] { 0x01, 0x00, 0x00, 0x00 }); // version

      tXRaw.Add(0x01); // #TxIn

      tXRaw.AddRange(new byte[32]); // TxOutHash

      tXRaw.AddRange("FFFFFFFF".ToBinary()); // TxOutIndex

      List<byte> blockHeight = VarInt.GetBytes(block.Header.Height); // Script coinbase
      tXRaw.Add((byte)blockHeight.Count);
      tXRaw.AddRange(blockHeight);

      tXRaw.AddRange("FFFFFFFF".ToBinary()); // sequence

      tXRaw.Add(0x01); // #TxOut

      ulong valueChange = (ulong)(50000 * 100e8);
      tXRaw.AddRange(BitConverter.GetBytes(valueChange));

      tXRaw.AddRange(UTXOTable.GetReceptionScript());

      tXRaw.AddRange(new byte[4]);


      int indexTXRaw = 0;
      byte[] tXRawArray = tXRaw.ToArray();

      TX tX = block.ParseTX(
        true,
        tXRawArray,
        ref indexTXRaw);

      tX.TXRaw = tXRawArray;

      block.TXs.Add(tX);

      return tX.Hash;
    }

    void ComputePoW(
      BlockBitcoin block,
      SHA256 sHA256,
      long nonceSeed)
    {
      HeaderBitcoin header = (HeaderBitcoin)block.Header;

      do
      {
        if (FlagMiningCancel)
          throw new TaskCanceledException();

        byte[] merkleRoot = LoadTXs(block);

        header.IncrementNonce(nonceSeed);

        header.CreateAppendingHeader(
          sHA256,
          merkleRoot,
          Blockchain.HeaderTip);

      } while (header.Hash.IsGreaterThan(header.NBits));
    }

    public override Block CreateBlock()
    {
      return new BlockBitcoin();
    }

    public override Block CreateBlock(int sizeBuffer)
    {
      return new BlockBitcoin(sizeBuffer);
    }

    public override bool TryRequestTX(
      byte[] hash, 
      out byte[] tXRaw)
    {
      TX tX = TXPool.Find(t => t.Hash.IsEqual(hash));

      if(tX == null)
      {
        tXRaw = null;
        return false;
      }

      tXRaw = tX.TXRaw;
      return true;
    }

    public override Header CreateHeaderGenesis()
    {
      HeaderBitcoin header = new(
         headerHash: "000000000019d6689c085ae165831e934ff763ae46a2a6c172b3f1b60a8ce26f".ToBinary(),
         version: 0x01,
         hashPrevious: "0000000000000000000000000000000000000000000000000000000000000000".ToBinary(),
         merkleRootHash: "4a5e1e4baab89f3a32518a88c31bc87f618f76673e2cc77ab2127b7afdeda33b".ToBinary(),
         unixTimeSeconds: 1231006505,
         nBits: 0x1d00ffff,
         nonce: 2083236893);

      header.Height = 0;
      header.DifficultyAccumulated = header.Difficulty;

      return header;
    }

    public override void LoadImage(string pathImage)
    {
      UTXOTable.LoadImage(pathImage);
    }

    public override void CreateImage(string pathImage)
    {
      UTXOTable.CreateImage(pathImage);
    }

    public override void Reset()
    {
      UTXOTable.Clear();
    }

    protected override void InsertInDatabase(Block block)
    {
      UTXOTable.InsertBlock(
        block.TXs,
        block.Header.IndexBlockArchive);
    }

    public override string GetStatus()
    {
      return
        Blockchain.GetStatus();
        //+ UTXOTable.GetStatus();
    }

    byte[] GetGenesisBlockBytes()
    {
      return new byte[285]{
        0x01, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00, 0x3b, 0xa3, 0xed, 0xfd, 0x7a, 0x7b, 0x12, 0xb2, 0x7a, 0xc7, 0x2c, 0x3e,
        0x67, 0x76, 0x8f, 0x61, 0x7f, 0xc8, 0x1b, 0xc3, 0x88, 0x8a, 0x51, 0x32, 0x3a, 0x9f, 0xb8, 0xaa,
        0x4b, 0x1e, 0x5e, 0x4a, 0x29, 0xab, 0x5f, 0x49, 0xff, 0xff, 0x00, 0x1d, 0x1d, 0xac, 0x2b, 0x7c,
        0x01, 0x01, 0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0xff, 0xff, 0xff, 0xff, 0x4d, 0x04, 0xff, 0xff, 0x00, 0x1d,
        0x01, 0x04, 0x45, 0x54, 0x68, 0x65, 0x20, 0x54, 0x69, 0x6d, 0x65, 0x73, 0x20, 0x30, 0x33, 0x2f,
        0x4a, 0x61, 0x6e, 0x2f, 0x32, 0x30, 0x30, 0x39, 0x20, 0x43, 0x68, 0x61, 0x6e, 0x63, 0x65, 0x6c,
        0x6c, 0x6f, 0x72, 0x20, 0x6f, 0x6e, 0x20, 0x62, 0x72, 0x69, 0x6e, 0x6b, 0x20, 0x6f, 0x66, 0x20,
        0x73, 0x65, 0x63, 0x6f, 0x6e, 0x64, 0x20, 0x62, 0x61, 0x69, 0x6c, 0x6f, 0x75, 0x74, 0x20, 0x66,
        0x6f, 0x72, 0x20, 0x62, 0x61, 0x6e, 0x6b, 0x73, 0xff, 0xff, 0xff, 0xff, 0x01, 0x00, 0xf2, 0x05,
        0x2a, 0x01, 0x00, 0x00, 0x00, 0x43, 0x41, 0x04, 0x67, 0x8a, 0xfd, 0xb0, 0xfe, 0x55, 0x48, 0x27,
        0x19, 0x67, 0xf1, 0xa6, 0x71, 0x30, 0xb7, 0x10, 0x5c, 0xd6, 0xa8, 0x28, 0xe0, 0x39, 0x09, 0xa6,
        0x79, 0x62, 0xe0, 0xea, 0x1f, 0x61, 0xde, 0xb6, 0x49, 0xf6, 0xbc, 0x3f, 0x4c, 0xef, 0x38, 0xc4,
        0xf3, 0x55, 0x04, 0xe5, 0x1e, 0xc1 ,0x12, 0xde, 0x5c, 0x38, 0x4d, 0xf7, 0xba, 0x0b, 0x8d, 0x57,
        0x8a, 0x4c, 0x70, 0x2b, 0x6b, 0xf1, 0x1d, 0x5f, 0xac, 0x00, 0x00 ,0x00 ,0x00 };
    }

    public override Header ParseHeader(
        byte[] buffer,
        ref int index)
    {
      BlockBitcoin bitcoinBlock = new();

      return bitcoinBlock.ParseHeader(
        buffer, 
        ref index);
    }
  }
}
