﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace BTokenCore.Chaining
{
  partial class Blockchain
  {
    public interface IBlockParser
    {
      public Block ParseBlock(
        byte[] buffer,
        ref int startIndex);
    }
  }
}