using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Diagnostics;
using Org.BouncyCastle.Asn1.X509;

namespace BTokenLib
{
  public class TXPool
  {
    const bool FLAG_ENABLE_RBF = true;

    Dictionary<byte[], List<(TXInput, TX)>> InputsPool =
      new(new EqualityComparerByteArray());

    Dictionary<byte[], TX> TXPoolDict = 
      new(new EqualityComparerByteArray());

    readonly object LOCK_TXsPool = new();

    public TXPool()
    { }

    /// <summary>
    /// Removes a tX and all tXs that reference its outputs.
    /// </summary>
    public void RemoveTXRecursive(byte[] hashTX)
    {
      if (TXPoolDict.Remove(hashTX, out TX tX))
      {
        List<(TXInput input, TX)> tupelInputs = null;

        foreach (TXInput tXInput in tX.TXInputs)
          if (InputsPool.TryGetValue(tXInput.TXIDOutput, out tupelInputs))
          {
            tupelInputs.RemoveAll(t => t.input.OutputIndex == tXInput.OutputIndex);

            if (tupelInputs.Count == 0)
              InputsPool.Remove(tXInput.TXIDOutput);
          }

        if (InputsPool.TryGetValue(hashTX, out tupelInputs))
          foreach ((TXInput input, TX tX) tupelInputInPool in tupelInputs.ToList())
            RemoveTXRecursive(tupelInputInPool.tX.Hash);
      }
    }

    public void RemoveTXs(IEnumerable<byte[]> hashesTX)
    {
      lock (LOCK_TXsPool)
        foreach (byte[] hashTX in hashesTX)
          RemoveTXRecursive(hashTX);
    }


    List<TX> TXsGet = new();
    int CountMaxTXsGet;

    public List<TX> GetTXs(out int countTXsPool, int countMax)
    {
      lock (LOCK_TXsPool)
      {
        TXsGet.Clear();
        CountMaxTXsGet = countMax;

        countTXsPool = TXPoolDict.Count;

        foreach (KeyValuePair<byte[], TX> tXInPool in TXPoolDict)
          if (TXsGet.Count < CountMaxTXsGet)
            ExtractBranch(tXInPool.Value);
          else
            break;

        return TXsGet.ToList();
      }
    }

    void ExtractBranch(TX tXRoot)
    {
      if (TXsGet.Contains(tXRoot))
        return;

      List<TX> tXsBranch = new() { tXRoot };

      TraceTXToLeaf(tXsBranch);

      foreach(TX tXBranch in tXsBranch)
      {
        foreach (TXInput input in tXBranch.TXInputs)
        {
          if (TXsGet.Count >= CountMaxTXsGet)
            return;

          if (TXPoolDict.TryGetValue(input.TXIDOutput, out TX tXRootSubBranch))
            ExtractBranch(tXRootSubBranch);
        }

        TXsGet.Add(tXBranch);
      }
    }

    void TraceTXToLeaf(List<TX> tXsBranch)
    {
      foreach (TXInput input in tXsBranch[0].TXInputs)
        if (TXPoolDict.TryGetValue(input.TXIDOutput, out TX tXInPool) &&
            !TXsGet.Contains(tXInPool))
        {
          tXsBranch.Insert(0, tXInPool);
          TraceTXToLeaf(tXsBranch);
          return;
        }
    }

    public bool AddTX(TX tX)
    {
      bool flagRemoveTXInPoolbeingRBFed = false;
      TX tXInPoolBeingRBFed = null;

      lock (LOCK_TXsPool)
      {
        foreach (TXInput tXInput in tX.TXInputs)
          if (InputsPool.TryGetValue(tXInput.TXIDOutput, out List<(TXInput, TX)> inputsInPool))
            foreach ((TXInput input, TX tX) tupelInputsInPool in inputsInPool)
              if (tupelInputsInPool.input.OutputIndex == tXInput.OutputIndex)
              {
                Debug.WriteLine(
                  $"Output {tXInput.TXIDOutput.ToHexString()} / {tXInput.OutputIndex} referenced by tX {tX} " +
                  $"already spent by tX {tupelInputsInPool.tX}.");

                if (
                  FLAG_ENABLE_RBF &&
                  tXInput.Sequence > tupelInputsInPool.input.Sequence)
                {
                  Debug.WriteLine(
                    $"Replace tX {tupelInputsInPool.tX} (sequence = {tupelInputsInPool.input.Sequence}) " +
                    $"with tX {tX} (sequence = {tXInput.Sequence}).");

                  flagRemoveTXInPoolbeingRBFed = true;
                  tXInPoolBeingRBFed = tupelInputsInPool.tX;
                }
                else
                  return false;
              }

        if (flagRemoveTXInPoolbeingRBFed)
          RemoveTXRecursive(tXInPoolBeingRBFed.Hash);

        TXPoolDict.Add(tX.Hash, tX);

        foreach (TXInput tXInput in tX.TXInputs)
          if (InputsPool.TryGetValue(tXInput.TXIDOutput, out List<(TXInput input, TX)> inputsInPool))
            inputsInPool.Add((tXInput,tX));
          else
            InputsPool.Add(tXInput.TXIDOutput, new List<(TXInput, TX)>() { (tXInput, tX) });

        return true;
      }
    }

    public int GetCountTXs()
    {
      lock (LOCK_TXsPool)
        return TXPoolDict.Count;
    }
  }
}
