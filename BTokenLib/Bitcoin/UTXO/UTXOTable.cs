using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;


namespace BTokenLib
{
  partial class UTXOTable
  {
    const int HASH_BYTE_SIZE = 32;
    const int COUNT_BATCHINDEX_BITS = 16;
    const int COUNT_COLLISION_BITS_PER_TABLE = 2;
    const int COUNT_COLLISIONS_MAX = 2 ^ COUNT_COLLISION_BITS_PER_TABLE - 1;

    const int LENGTH_BITS_UINT = 32;
    const int LENGTH_BITS_ULONG = 64;

    public const int COUNT_NON_OUTPUT_BITS =
      COUNT_BATCHINDEX_BITS + 
      COUNT_COLLISION_BITS_PER_TABLE * 3;

    uint MaskBatchIndexUInt32 = ~(uint.MaxValue << COUNT_BATCHINDEX_BITS);
    ulong MaskBatchIndexULong64 = ~(ulong.MaxValue << COUNT_BATCHINDEX_BITS);
       
    UTXOIndex[] Tables;
    UTXOIndexUInt32 TableUInt32 = new();
    UTXOIndexULong64 TableULong64 = new();
    UTXOIndexUInt32Array TableUInt32Array = new();

    StreamWriter LogFile;



    public UTXOTable()
    {
      LogFile = new StreamWriter("logUTXOTable", false);

      Tables = new UTXOIndex[]{
        TableUInt32,
        TableULong64,
        TableUInt32Array };
    }

    public void LoadImage(string pathImageRoot)
    {
      string pathImage = Path.Combine(
        pathImageRoot, 
        "UTXOImage");

      for (int c = 0; c < Tables.Length; c += 1)
      {
        $"Load UTXO Table {Tables[c].GetType().Name}.".Log(LogFile);
        Tables[c].LoadImage(pathImage);
      }

      $"Load UTXO Image from {pathImage}".Log(LogFile);
    }

    public void CreateImage(string path)
    {
      string pathUTXOImage = Path.Combine(path, "UTXOImage");
      DirectoryInfo directoryUTXOImage = new(pathUTXOImage);

      Parallel.ForEach(Tables, t =>
      {
        t.BackupImage(directoryUTXOImage.FullName);
      });
    }

    public string GetStatus()
    {
      return
        Tables[0].GetStatus() + "," +
        Tables[1].GetStatus() + "," +
        Tables[2].GetStatus();
    }

    public void Clear()
    {
      for (int c = 0; c < Tables.Length; c += 1)
        Tables[c].Clear();
    }

    public void InsertBlock(
      List<TX> tXs,
      int indexArchive)
    {
      for (int t = 0; t < tXs.Count; t++)
      {
        int lengthUTXOBits =
          COUNT_NON_OUTPUT_BITS +
          tXs[t].TXOutputs.Count;

        if (LENGTH_BITS_UINT >= lengthUTXOBits)
        {
          uint uTXOIndex = 0;

          if (LENGTH_BITS_UINT > lengthUTXOBits)
            uTXOIndex |= uint.MaxValue << lengthUTXOBits;

          TableUInt32.UTXO =
            uTXOIndex | (uint)indexArchive & MaskBatchIndexUInt32;

          try
          {
            InsertUTXO(
              tXs[t].Hash,
              tXs[t].TXIDShort,
              TableUInt32);
          }
          catch (ArgumentException)
          {
            // BIP 30
            if (tXs[t].Hash.ToHexString() == "D5D27987D2A3DFC724E359870C6644B40E497BDC0589A033220FE15429D88599" ||
               tXs[t].Hash.ToHexString() == "E3BF3D07D4B0375638D5F1DB5255FE07BA2C4CB067CD81B84EE974B6585FB468")
            {
              Console.WriteLine("Implement BIP 30.");
            }
          }
        }
        else if (LENGTH_BITS_ULONG >= lengthUTXOBits)
        {
          ulong uTXOIndex = 0;

          if (LENGTH_BITS_ULONG > lengthUTXOBits)
            uTXOIndex |= ulong.MaxValue << lengthUTXOBits;

          TableULong64.UTXO =
            uTXOIndex | (ulong)indexArchive & MaskBatchIndexULong64;

          InsertUTXO(
            tXs[t].Hash,
            tXs[t].TXIDShort,
            TableULong64);
        }
        else
        {
          uint[] uTXOIndex = new uint[(lengthUTXOBits + 31) / 32];

          int countUTXORemainderBits = lengthUTXOBits % 32;
          if (countUTXORemainderBits > 0)
            uTXOIndex[uTXOIndex.Length - 1] |= uint.MaxValue << countUTXORemainderBits;

          TableUInt32Array.UTXO = uTXOIndex;
          TableUInt32Array.UTXO[0] |= (uint)indexArchive & MaskBatchIndexUInt32;

          InsertUTXO(
            tXs[t].Hash,
            tXs[t].TXIDShort,
            TableUInt32Array);
        }
      }

      for (int t = 0; t < tXs.Count; t++)
      {
        for (int i = 0; i < tXs[t].TXInputs.Count; i++)
        {
          for (int tb = 0; tb < Tables.Length; tb++)
          {
            if (Tables[tb].TryGetValueInPrimaryTable(
              tXs[t].TXInputs[i].TXIDOutputShort))
            {
              UTXOIndex tableCollision = null;

              for (int cc = 0; cc < Tables.Length; cc += 1)
                if (Tables[tb].HasCollision(cc))
                {
                  tableCollision = Tables[cc];

                  if (tableCollision.TrySpendCollision(
                    tXs[t].TXInputs[i],
                    Tables[tb]))
                  {
                    goto LABEL_LoopNextInput;
                  }
                }

              Tables[tb].SpendPrimaryUTXO(
                tXs[t].TXInputs[i],
                out bool allOutputsSpent);

              if (allOutputsSpent)
              {
                Tables[tb].RemovePrimary();

                if (tableCollision != null)
                  tableCollision.ResolveCollision(Tables[tb]);
              }

              goto LABEL_LoopNextInput;
            }
          }

          throw new ProtocolException(
            $"Referenced TX {tXs[t].TXInputs[i].TXIDOutput.ToHexString()} " +
            $"not found in UTXO table.");

        LABEL_LoopNextInput:;
        }
      }
    }

    void InsertUTXO(
      byte[] uTXOKey,
      int primaryKey,
      UTXOIndex table)
    {
      for (int c = 0; c < Tables.Length; c += 1)
        if (Tables[c].PrimaryTableContainsKey(primaryKey))
        {
          Tables[c].IncrementCollisionBits(
            primaryKey,
            table.Address);

          table.AddUTXOAsCollision(uTXOKey);

          return;
        }

      table.AddUTXOAsPrimary(primaryKey);
    }
  }
}
