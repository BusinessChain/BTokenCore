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



    public TokenBitcoin(string pathBlockArchive)
      : base(pathBlockArchive)
    {
      UTXOTable = new();
    }


    const int LENGTH_P2PKH = 25;
    byte OP_RETURN = 0x6A;
    byte[] PREFIX_P2PKH = new byte[] { 0x76, 0xA9, 0x14 };

    byte[] POSTFIX_P2PKH = new byte[] { 0x88, 0xAC };
    byte[] PublicKeyHash160 = new byte[20];

    public override TX CreateDataTX(byte[] dataOPReturn)
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

      BlockBitcoin parser = new();
      int indexTXRaw = 0;
      byte[] tXRawArray = tXRaw.ToArray();

      TX tX = parser.ParseTX(
        false,
        tXRawArray,
        ref indexTXRaw);

      tX.TXRaw = tXRawArray;

      return tX;
    }


    public override void StartMining()
    {
      if (IsMining)
        return;

      IsMining = true;

      int numberOfProcesses = Math.Max(Environment.ProcessorCount - 1, 1);
      long nonceSegment = uint.MaxValue / numberOfProcesses;

      Parallel.For(
        0,
        numberOfProcesses,
        i => RunMining(i * nonceSegment));

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

      tXRaw.AddRange(Wallet.GetReceptionScript());

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
         headerHash: "0000000000000000000d37dfef7fe1c7bd22c893dbe4a94272c8cf556e40be99".ToBinary(),
         version: 0x01,
         hashPrevious: "000000000000000000029da63650d127e160033c93393da77302320bd8ee4958".ToBinary(),
         merkleRootHash: "a118d95c5a2f17d50a5bc10a0968476af7d1c19905963d86b65b50144850c26c".ToBinary(),
         unixTimeSeconds: 1634588757,
         nBits: 0x170E0408,
         nonce: 2083236893);

      header.Height = 705600;
      header.DifficultyAccumulated = header.Difficulty;

      return header;
    }

    public override void LoadImage(string pathImage)
    {
      UTXOTable.LoadImage(pathImage);
      Wallet.LoadImage(pathImage);
    }

    public override void CreateImage(string pathImage)
    {
      UTXOTable.CreateImage(pathImage);

      Wallet.CreateImage(pathImage);
    }

    public override void Reset()
    {
      UTXOTable.Clear();
    }

    protected override void InsertInDatabase(Block block)
    {
      UTXOTable.InsertBlock(
        block.TXs,
        block.Header.IndexBlockArchive,
        Wallet);
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
