using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace TsRemux
{
    // Base class for data muxer.
    abstract public class Muxer
    {
        // Called with every used / valid PesPacket from source stream.
        public abstract void MuxPacket(PesPacket pp);

        // Finish muxing.
        public abstract void Close();

        // Blu-Ray authoring only.
        public abstract EpElement[] GetEpData();
        // Blu-Ray authoring only.
        public abstract StreamInfo[] GetPsi();
        // Blu-Ray authoring only.
        public abstract UInt32 GetCurrentPacketNumber();

        // Called if source changes PCR.
        public abstract void PcrChanged(Int64 pcr);

        public StreamInfo[] Psi
        {
            get { return GetPsi(); }
        }

        public EpElement[] EpData
        {
            get { return GetEpData(); }
        }

        public UInt32 CurrentPacketNumber
        {
            get { return GetCurrentPacketNumber(); }
        }
    }
}
