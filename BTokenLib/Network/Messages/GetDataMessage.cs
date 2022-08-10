using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;


namespace BTokenLib
{
  partial class Network
  {
    class GetDataMessage : NetworkMessage
    {
      public List<Inventory> Inventories = new();



      public GetDataMessage(byte[] buffer)
        : base("getdata", buffer)
      {
        int startIndex = 0;

        int inventoryCount = VarInt.GetInt32(
          Payload,
          ref startIndex);

        for (int i = 0; i < inventoryCount; i += 1)
          Inventories.Add(Inventory.Parse(
            Payload,
            ref startIndex));
      }


      public GetDataMessage(List<Inventory> inventories)
        : base("getdata")
      {
        Inventories = inventories;

        List<byte> payload = new();

        payload.AddRange(VarInt.GetBytes(Inventories.Count()));

        for (int i = 0; i < Inventories.Count(); i++)
          payload.AddRange(Inventories[i].GetBytes());

        Payload = payload.ToArray();
        LengthDataPayload = Payload.Length;
      }
    }
  }
}
