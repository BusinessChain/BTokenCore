using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace BTokenCore
{
  class RecordDBAccounts
  {
    public uint CountdownToReplay; // was ist wenn ein Account value null wird und aus der DB gelöscht wird?
    // wenn später der account wieder geöffnet wird muss sicher sein dass nicht wieder dieselben nonces gebraucht werden
    // deshalb muss in der nonce noch die blockheight der account eröffnung drin stehen, also besser ulong
    public long Value;
    public byte[] IDAccount;
  }
}