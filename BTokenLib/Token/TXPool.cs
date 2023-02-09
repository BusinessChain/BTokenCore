using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Diagnostics;

namespace BTokenLib
{
  public class TXPool
  {
    Dictionary<byte[], List<(TXInput, TX)>> InputsPool =
      new(new EqualityComparerByteArray());

    Dictionary<byte[], TX> TXPoolDict = 
      new(new EqualityComparerByteArray());

    readonly object LOCK_TXsPool = new();

    public TXPool()
    { }

    public void RemoveTX(byte[] hashTX)
    {
      if (TXPoolDict.TryGetValue(hashTX, out TX tXInPool))
      {
        foreach (TXInput tXInput in tXInPool.TXInputs)
        {
          List<(TXInput input, TX)> tupelInputsInPool = InputsPool[tXInput.TXIDOutput];

          tupelInputsInPool.RemoveAll(t => t.input.OutputIndex == tXInput.OutputIndex);

          if (tupelInputsInPool.Count == 0)
            InputsPool.Remove(tXInput.TXIDOutput);
        }

        TXPoolDict.Remove(hashTX);
      }

    }

    public void RemoveTXs(IEnumerable<byte[]> hashesTX)
    {
      lock (LOCK_TXsPool)
        foreach (byte[] hashTX in hashesTX)
          RemoveTX(hashTX);
    }

    public IEnumerable<TX> GetTXs(out int countTXs)
    {
      lock (LOCK_TXsPool)
      {
        countTXs = TXPoolDict.Count;
        return TXPoolDict.ToList().Select(k => k.Value);
      }
    }

    // TXPoolList soll ein dict sein. Dann kann bei gleichem Input die Sequenznummer
    // berücksichtigt werden. Tests machen mit und ohne Sequenznummer berüclsichtigung
    // beides muss funktioneren.
    public bool AddTX(TX tX)
    {
      lock (LOCK_TXsPool)
      {
        bool flagRemoveTXInPoolbeingRBFed = false;
        TX tXInPoolBeingRBFed = null;

        foreach (TXInput tXInput in tX.TXInputs)
        {
          if(TXPoolDict.TryGetValue(tXInput.TXIDOutput, out TX tXInPoolWithReferencedOutput))
          {
            TXOutput output = tXInPoolWithReferencedOutput.TXOutputs[tXInput.OutputIndex];
            // check signature
          }
          else
          {
            Debug.WriteLine(
              $"Output {tXInput.TXIDOutput} / {tXInput.OutputIndex}" +
              $"referenced by tX {tX} not in pool.");

            return false;
          }

          if (InputsPool.TryGetValue(tXInput.TXIDOutput, out List<(TXInput, TX)> inputsInPool))
            foreach ((TXInput input, TX tX) tupelInputsInPool in inputsInPool)
              if (tupelInputsInPool.input.OutputIndex == tXInput.OutputIndex)
              {
                Debug.WriteLine(
                  $"TX input referencing {tXInput.TXIDOutput} / {tXInput.OutputIndex}" +
                  $"of tX {tX} already in pool.");

                if (tXInput.Sequence > tupelInputsInPool.input.Sequence)
                {
                  flagRemoveTXInPoolbeingRBFed = true;
                  tXInPoolBeingRBFed = tupelInputsInPool.tX;
                }
                else
                  return false;
              }
        }

        if(flagRemoveTXInPoolbeingRBFed)
          RemoveTX(tXInPoolBeingRBFed.Hash);

        foreach (TXInput tXInput in tX.TXInputs)
          if (InputsPool.TryGetValue(tXInput.TXIDOutput, out List<(TXInput input, TX)> inputsInPool))
            inputsInPool.Add((tXInput,tX));
          else
            InputsPool.Add(tXInput.TXIDOutput, new List<(TXInput, TX)>() { (tXInput, tX) });

        TXPoolDict.Add(tX.Hash, tX);

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
