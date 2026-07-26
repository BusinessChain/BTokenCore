using System;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Collections.Generic;


namespace BTokenLib
{
  public abstract partial class Token
  {
    public abstract class TXOutput
    {
      public enum TypesToken
      {
        Unspecified = 0x00,
        P2PKH = 0x01
      }

      public byte[] IDAccount;

      public byte[] Script;

      public long Value;

      public TypesToken Type;
    }
  }
}
