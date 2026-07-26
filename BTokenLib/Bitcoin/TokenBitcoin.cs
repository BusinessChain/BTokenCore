using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Security.Cryptography;


namespace BTokenLib
{
  public partial class TokenBitcoin : Token
  {
    class EqualityComparerTXOutputWallet : IEqualityComparer<TXOutputWallet>
    {
      public bool Equals(TXOutputWallet x, TXOutputWallet y)
      {
        return x.Index == y.Index && x.TXID.IsAllBytesEqual(y.TXID);
      }

      public int GetHashCode(TXOutputWallet x)
      {
        return BitConverter.ToInt32(x.TXID, 0) + x.Index;
      }
    }

    const int SIZE_BLOCK_MAX = 1 << 20; // 1 MB

    public const long BLOCK_REWARD_INITIAL = 5000000000;
    public const int PERIOD_HALVENING_BLOCK_REWARD = 210000;

    public const int LENGTH_P2PKH_INPUT = 148;
    public const int LENGTH_P2PKH_OUTPUT = 34;
    public const int LENGTH_TX_OVERHEAD = 10;

    List<TXOutputWallet> OutputsSpendable = new();
    List<TXOutputWallet> OutputsSpendableConfirmed = new();


    public TokenBitcoin(ILogEntryNotifier logEntryNotifier)
      : base(logEntryNotifier)
    {
      //Wallet = new WalletBitcoin(File.ReadAllText($"Wallet{GetName()}/wallet"), this);

      SizeBlockMax = SIZE_BLOCK_MAX;

      IDToken = new byte[3] { (byte)'B', (byte)'T', (byte)'C' };

      Network = new NetworkToken(
        null,
        this,
        port: 8333,
        flagEnableInboundConnections: false,
        flagEnableRelay: false);
    }

    public override Header CreateHeaderGenesis()
    {
      //HeaderBitcoin header = new(
      //   headerHash: "0000000000000000000230d9bb1db81e56916b0c2c7363231e75b82b24714482".ToBinary(),
      //   version: 0x01,
      //   hashPrevious: "00000000000000000008b5ffa0ae1b604dd27bf4af84602ea53f7920320a3c96".ToBinary(),
      //   merkleRootHash: "ef303d1cf8090e1bcea36432eceea2bbc156e81108deff1616d9c6dee64ba7c7".ToBinary(),
      //   unixTimeSeconds: 1653490985, // take timestamp from trezor.io explorer and convert to epoch time GMT
      //   nBits: 386492960,
      //   nonce: 578608666);

      //header.Height = 737856; // Should be modulo 2016 so it calculates next target bits correctly.

      HeaderBitcoin header = new HeaderBitcoin(
         headerHash: "000000A13F15EC9FECECAB8EF438F8E16E729AC2AF816C3DBE7E27BAF110F66A".ToBinary(),
         version: 0x01,
         hashPrevious: "0000000000000000000000000000000000000000000000000000000000000000".ToBinary(),
         merkleRootHash: "0000000000000000000000000000000000000000000000000000000000000000".ToBinary(),
         unixTimeSeconds: 1667333891,
         //nBits: 0x1d4fffff,
         nBits: 0x1dffffff,
         nonce: 1441757173);

      header.Height = 0; // Should be modulo 2016 so it calculates next target bits correctly.

      header.DifficultyAccumulated = header.Difficulty;

      return header;
    }

    public override Header ParseHeader(byte[] buffer, ref int index, SHA256 sHA256)
    {
      byte[] hash = sHA256.ComputeHash(
          sHA256.ComputeHash(
            buffer,
            index,
            HeaderBitcoin.COUNT_HEADER_BYTES));

      uint version = BitConverter.ToUInt32(buffer, index);
      index += 4;

      byte[] hashHeaderPrevious = new byte[32];
      Array.Copy(buffer, index, hashHeaderPrevious, 0, 32);
      index += 32;

      byte[] merkleRootHash = new byte[32];
      Array.Copy(buffer, index, merkleRootHash, 0, 32);
      index += 32;

      uint unixTimeSeconds = BitConverter.ToUInt32(buffer, index);
      index += 4;

      bool isBlockTimePremature = unixTimeSeconds >
        (DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 2 * 60 * 60);

      if (isBlockTimePremature)
        throw new ProtocolException($"Timestamp premature {new DateTime(unixTimeSeconds).Date}.");

      uint nBits = BitConverter.ToUInt32(buffer, index);
      index += 4;

      if (hash.IsGreaterThan(nBits))
        throw new ProtocolException($"Header hash {hash.ToHexString()} greater than NBits {nBits}.");

      uint nonce = BitConverter.ToUInt32(buffer, index);
      index += 4;

      return new HeaderBitcoin(
        hash,
        version,
        hashHeaderPrevious,
        merkleRootHash,
        unixTimeSeconds,
        nBits,
        nonce);
    }

    public override TX ParseTX(byte[] buffer, ref int index, SHA256 sHA256, bool flagIsCoinbase)
    {
      return new TXBitcoin(buffer, ref index, sHA256, flagIsCoinbase);
    }

    public override List<string> GetSeedAddresses()
    {
      return new List<string>()
        {"83.229.86.158" 
        // 84.74.69.100
            //"167.179.147.155","95.89.103.28","2.59.236.56", "49.64.10.128", "91.219.25.232",
            //"3.8.174.255", "93.216.78.178", "88.99.209.7", "93.104.126.120", "47.149.50.194",
            //"18.183.139.213", "49.64.10.100", "49.12.82.82", "3.249.250.35", "86.220.37.55",
            //"147.194.177.165", "5.9.42.21", "75.56.8.205","86.166.110.213","35.201.215.214",
            //"88.70.152.28", "97.84.96.62", "185.180.196.74","34.101.105.12", "77.21.236.207",
            //"93.177.82.226", "51.75.61.18", "51.75.144.201", "185.46.17.66", "50.98.185.178",
            //"31.14.40.64", "185.216.178.92", "173.230.133.14", "50.39.164.136", "13.126.144.12",
            //"149.90.214.78", "66.208.64.128", "37.235.134.102", "18.141.198.180", "62.107.200.30",
            //"162.0.216.227", "85.10.206.119", "95.164.65.194", "35.196.81.199", "85.243.55.37",
            //"167.172.151.136", "86.89.77.44", "221.140.248.61", "62.171.166.70", "90.146.130.214",
            //"70.183.190.131", "84.39.176.10", "89.33.195.97", "165.22.224.124", "87.220.77.134",
            //"141.94.74.233", "73.108.241.200", "73.108.241.200", "87.184.110.132", "34.123.171.121",
            //"85.149.70.74", "167.172.41.211", "85.165.8.197", "157.90.133.235", "185.73.200.134",
            //"68.37.223.44", "79.98.159.7", "79.98.159.7", "63.224.37.22", "94.23.248.168",
            //"195.213.137.231", "3.248.215.13", "195.201.56.56", "51.210.61.169", "5.166.54.83",
            //"3.137.140.0", "3.17.174.43", "84.112.177.143", "173.249.0.235", "178.63.52.122",
            //"3.112.22.239", "168.119.249.19", "162.55.3.214", "194.147.113.201", "5.9.99.119",
            //"209.133.222.18", "217.20.194.197", "3.248.223.135", "165.22.224.124", "45.88.188.220",
            //"188.166.120.198", "5.128.87.126", "5.9.2.199", "185.85.3.140", "3.219.41.205",
            //"185.25.48.7", "62.210.123.142", "15.237.117.105", "82.71.47.216", "161.97.84.78",
            //"86.9.128.254","134.209.74.26", "35.181.8.232", "168.119.18.7", "144.76.1.155",
            //"78.129.201.15", "65.108.70.139", "51.210.208.70", "79.137.68.161", "85.241.35.244",
            //"34.106.217.38", "192.222.24.54", "165.22.235.29", "18.221.170.34", "23.95.207.214",
            //"138.201.56.226", "193.234.50.227", "5.254.56.74", "213.214.66.182", "95.211.152.100",
            //"84.92.92.247", "169.0.168.95", "178.164.201.40", "190.2.149.82", "195.214.133.15",
            //"199.247.227.180", "201.210.177.231", "207.191.102.93", "212.93.114.22", "45.183.140.233",
            //"47.198.204.108", "81.251.223.139", "84.85.227.8", "88.207.124.229", "91.56.252.34",
            //"92.116.38.250", "95.56.63.179", "97.115.103.114", "98.143.78.109", "98.194.50.84",
            //"134.19.118.183", "152.169.255.224",
        };
    }

    public override bool TryGetTX(byte[] hash, out TX tX)
    {
      tX = null;
      return false;
    }

    public override bool TryCreateTXAnchor(TXOutputTokenAnchor tokenAnchor, long feePerByte, out TX tXAnchor)
    {
      tXAnchor = new TXBitcoin();

      //return new()
      //{
      //  new TXOutputWallet()
      //  {
      //    TXID = "20da7491ec53757a914dc1f045afbcb0a5c3396785a9abe9fc074e017e9403fd".ToBinary(),
      //    Value = 7106,
      //    Index = 1
      //  }
      //};

      long feePerInputP2PKH = (LENGTH_P2PKH_INPUT * feePerByte);
      long feePerOutputP2PKH = (LENGTH_P2PKH_OUTPUT * feePerByte);

      List<TXOutputWallet> outputsSpendable = OutputsSpendableConfirmed
        .Where(o => o.Value > feePerInputP2PKH)
        .Take(VarInt.PREFIX_UINT16 - 1).ToList();

      long valueInputs = outputsSpendable.Sum(o => o.Value);

      byte[] tokenAnchorRaw = tokenAnchor.Serialize();

      long feeTX = (long)(feePerByte
        * (LENGTH_P2PKH_INPUT * outputsSpendable.Count
        + LENGTH_TX_OVERHEAD
        + tokenAnchorRaw.Length));

      if (valueInputs < feeTX + tokenAnchor.Value)
        return false;

      long valueChange = valueInputs - tokenAnchor.Value - feeTX - feePerOutputP2PKH;

      bool flagCreateOutputChange = valueChange > feePerInputP2PKH;

      foreach (TXOutputWallet outputSpendable in outputsSpendable)
      {
        ((TXBitcoin)tXAnchor).Inputs.Add(new TXInputBitcoin
        {
          TXIDOutput = outputSpendable.TXID,
          OutputIndex = outputSpendable.Index
        });
      }

      tXAnchor.TXOutputs.Add(tokenAnchor);

      if (flagCreateOutputChange)
        tXAnchor.TXOutputs.Add(new TXOutputBitcoin
        {
          Type = TXOutput.TypesToken.P2PKH,
          Value = valueChange,
          Script = PREFIX_P2PKH.Concat(Wallet.Hash160PKeyPublic).Concat(POSTFIX_P2PKH).ToArray()
        });

      tXAnchor.Serialize(Wallet);

      return true;
    }


    // Hier darf es keine Exception geben, weil wir einen
    // geparsten Bitcoin Block mit PoW immer als korrekt betrachten
    
    public override void InsertBlock(Block block)
    {
      foreach (TXBitcoin tX in block.TXs)
      {
        foreach (TXInputBitcoin tXInput in tX.Inputs)
          OutputsSpendable.RemoveAll(o => o.TXID.IsAllBytesEqual(tXInput.TXIDOutput) && o.Index == tXInput.OutputIndex);

        for (int i = 0; i < tX.TXOutputs.Count; i++)
          if (TryAddTXOutputWallet(OutputsSpendable, tX, i))

        IndexTXs.Add(tX.Hash, tX);
      }
    }

    // Das muss eine Datanbank sein!!
    public Dictionary<byte[], TX> IndexTXs = new(new EqualityComparerByteArray());

    public override void ReverseBlock(Block block)
    {
      for (int t = block.TXs.Count - 1; t >= 0; t--)
      {
        TXBitcoin tX = block.TXs[t] as TXBitcoin;

        OutputsSpendable.RemoveAll(o => o.TXID.IsAllBytesEqual(tX.Hash));

        foreach (TXInputBitcoin tXInput in tX.Inputs)
        {
          TX tXReferenced = block.TXs.Find(t => t.Hash.IsAllBytesEqual(tXInput.TXIDOutput));

          if (tXReferenced != null || IndexTXs.TryGetValue(tXInput.TXIDOutput, out tXReferenced))
            TryAddTXOutputWallet(OutputsSpendable, tXReferenced as TXBitcoin, tXInput.OutputIndex);
        }

        IndexTXs.Remove(tX.Hash);
      }
    }

    bool TryAddTXOutputWallet(List<TXOutputWallet> listOutputs, TXBitcoin tX, int indexOutput)
    {
      TXOutputBitcoin tXOutputReferenced = (TXOutputBitcoin)tX.TXOutputs[indexOutput];

      if (tXOutputReferenced.Type == TXOutput.TypesToken.P2PKH &&
        tXOutputReferenced.PublicKeyHash160.IsAllBytesEqual(Wallet.Hash160PKeyPublic))
      {
        listOutputs.Add(
          new TXOutputWallet
          {
            TXID = tX.Hash,
            Index = indexOutput,
            Value = tXOutputReferenced.Value
          });

        return true;
      }

      return false;
    }
  }
}
