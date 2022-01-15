﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;


namespace BTokenCore
{
  partial class UTXOTable
  {
    public class TX : BTokenLib.TX
    {
      public int TXIDShort;
      public List<TXInput> TXInputs = new List<TXInput>();
      public List<TXOutput> TXOutputs = new List<TXOutput>();
    }
  }
}
