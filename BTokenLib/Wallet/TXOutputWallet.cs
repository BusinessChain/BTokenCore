using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;



namespace BTokenLib
{
  public class TXOutputWallet
  {
    public byte[] TXID = new byte[32];
    public int TXIDShort;
    public int OutputIndex;
    public long Value;
    public byte[] ScriptPubKey = new byte[32];
  }
}
