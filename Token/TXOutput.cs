using System;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Collections.Generic;


namespace BTokenCore;

public abstract class TXOutput
{
  internal enum TypesToken
  {
    Unspecified = 0x00,
    P2PKH = 0x01
  }

  internal byte[] IDAccount;

  internal byte[] Script;

  internal long Value;

  internal TypesToken Type;
}
