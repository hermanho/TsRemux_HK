using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace TsRemux
{
    public class Coordinator
    {
        private PesFile inFile;
        private Muxer outFile;
        Int64 endTrim;
        BackgroundWorker bw;
        bool done;
        Int64 seconds;
        bool subtitles;
        Int64 startOffset;

        public Coordinator()
        {
            inFile = null;
            outFile = null;
            done = false;
            endTrim = -1;
            bw = null;
            seconds = 0;
            subtitles = false;
            startOffset = 0;
        }

        public void StartMuxing(string outPath, BackgroundWorker worker, TsFileType outType, List<ushort> pidsToKeep, TimeSpan ts, TimeSpan te, bool useAsync, PesFile input)
        {
            StartMuxing(outPath, worker, outType, pidsToKeep, ts, te, useAsync, false, false, input, null, TimeSpan.Zero, TimeSpan.Zero);
        }

        public void StartMuxing(string outPath, BackgroundWorker worker, TsFileType outType, List<ushort> pidsToKeep, TimeSpan ts, TimeSpan te, bool useAsync, bool processAudio, bool MlpToAc3, PesFile input, PesFile secondary, TimeSpan offset, TimeSpan chapterLen)
        {
            inFile = input;
            bw = worker;
            List<StreamInfo> sis = new List<StreamInfo>();
            PesPacket[] ppa = null;
            PesPacket[] spa = null;
            BluRayOutput bo = null;

            Int64 startTrim = inFile.StartPcr + ((Int64)ts.TotalSeconds * (Int64)Constants.MPEG2TS_CLOCK_RATE);
            if (startTrim > Constants.MAX_MPEG2TS_CLOCK)
                startTrim -= Constants.MAX_MPEG2TS_CLOCK;
            foreach (StreamInfo si in inFile.StreamInfos)
                if (pidsToKeep.Contains(si.ElementaryPID))
                    sis.Add(si);
            if (null != secondary && pidsToKeep.Contains(secondary.StreamInfos[0].ElementaryPID))
                sis.Add(secondary.StreamInfos[0]);

            switch (outType)
            {
                case TsFileType.BLU_RAY:
                    bo = new BluRayOutput(outPath, chapterLen);
                    outFile = new BlueMux(Path.Combine(outPath, @"BDMV\STREAM\00001.m2ts"), outType, sis, useAsync, processAudio, MlpToAc3);
                    break;

                case TsFileType.MKV:
                    outFile = new MkvMux(outPath, sis, useAsync, processAudio);
                    break;

                case TsFileType.DEMUX:
                    outFile = new DeMux(outPath, sis, useAsync, processAudio);
                    break;

                default:
                    outFile = new BlueMux(outPath, outType, sis, useAsync, processAudio, MlpToAc3);
                    break;
            }

            if (ts.TotalSeconds == 0)
                inFile.Seek(-1);
            else
                inFile.Seek(startTrim);

            if (te.TotalSeconds > 0)
            {
                endTrim = inFile.EndPcr - ((Int64)te.TotalSeconds * (Int64)Constants.MPEG2TS_CLOCK_RATE);
                if (endTrim < 0)
                    endTrim += Constants.MAX_MPEG2TS_CLOCK;
            }
            inFile.SetPcrDelegate(new PcrChanged(UpdatePcr));
            inFile.SetPtsDelegate(new PtsChanged(UpdatePts));
            startOffset = startTrim + ((Int64)offset.TotalSeconds * (Int64)Constants.MPEG2TS_CLOCK_RATE);
            if (startOffset > Constants.MAX_MPEG2TS_CLOCK)
                startOffset -= Constants.MAX_MPEG2TS_CLOCK;
            for (ppa = inFile.GetNextPesPackets(); ppa != null && ppa.Length > 0 && subtitles == false; ppa = inFile.GetNextPesPackets())
            {
                if (worker.CancellationPending || done)
                    goto leave_routine;
                foreach (PesPacket pp in ppa)
                    if (null != pp)
                        outFile.MuxPacket(pp);
                    else
                        goto leave_routine;
            }
            if (subtitles)
            {
                if (null != secondary && ppa != null && ppa.Length > 0)
                {
                    secondary.Seek(-1);
                    spa = secondary.GetNextPesPackets();
                    PesPacket sp = spa[0];
                    PesHeader sh = sp.GetHeader();
                    PesHeader ph = ppa[0].GetHeader();
                    while (ph == null || ph.HasPts == false)
                    {
                        if (worker.CancellationPending || done)
                            goto leave_routine;
                        foreach (PesPacket pp in ppa)
                            if (null != pp)
                                outFile.MuxPacket(pp);
                        ppa = inFile.GetNextPesPackets();
                        if (ppa == null || ppa.Length == 0 || ppa[0] == null)
                            goto leave_routine;
                        ph = ppa[0].GetHeader();
                    }
                    Int64 ptsOffset = ph.Pts - sh.Pts;
                    bool clock = true;
                    for (; ppa != null && ppa.Length > 0 && ppa[0] != null; ppa = inFile.GetNextPesPackets())
                    {
                        foreach (PesPacket pp in ppa)
                        {
                            ph = pp.GetHeader();
                            if (sh != null && ph != null && ph.HasPts)
                            {
                                if (clock)
                                {
                                    Int64 time = sh.Pts + ptsOffset;
                                    if (time < 0)
                                        time += Constants.MAX_PTS_CLOCK;
                                    else if (time > Constants.MAX_PTS_CLOCK)
                                        time -= Constants.MAX_PTS_CLOCK;
                                    sh.Pts = time;
                                    for (int i = 9; i < 14; i++)
                                        sp[i] = sh[i]; // copy PTS
                                    if (sh.HasDts)
                                    {
                                        time = sh.Dts + ptsOffset;
                                        if (time < 0)
                                            time += Constants.MAX_PTS_CLOCK;
                                        else if (time > Constants.MAX_PTS_CLOCK)
                                            time -= Constants.MAX_PTS_CLOCK;
                                        sh.Dts = time;
                                        for (int i = 14; i < 19; i++)
                                            sp[i] = sh[i]; // copy DTS
                                    }
                                    clock = false;
                                }
                                Int64 delta = sh.Pts - ph.Pts;
                                if (delta > (0 - Constants.PTS_CLOCK_RATE) && delta < Constants.PTS_CLOCK_RATE)
                                {
                                    outFile.MuxPacket(sp);
                                    spa = secondary.GetNextPesPackets();
                                    if (spa != null && spa.Length > 0 && spa[0] != null)
                                    {
                                        sp = spa[0];
                                        sh = sp.GetHeader();
                                        clock = true;
                                    }
                                }
                            }
                            outFile.MuxPacket(pp);
                        }
                    }
                }
                else
                {
                    for (; ppa != null && ppa.Length > 0; ppa = inFile.GetNextPesPackets())
                    {
                        if (worker.CancellationPending || done)
                            goto leave_routine;
                        foreach (PesPacket pp in ppa)
                            if (null != pp)
                                outFile.MuxPacket(pp);
                            else
                                goto leave_routine;
                    }
                }
            }
        leave_routine:
            outFile.Close();
            if (outType == TsFileType.BLU_RAY)
            {
                bo.Author(outFile.EpData, outFile.Psi, outFile.CurrentPacketNumber);
            }
        }

        public void UpdatePcr(Int64 pcr)
        {
            if (subtitles == false)
            {
                Int64 delta = startOffset - pcr;
                if (delta > (0 - Constants.MPEG2TS_CLOCK_RATE) && delta < Constants.MPEG2TS_CLOCK_RATE)
                    subtitles = true;
            }
            outFile.PcrChanged(pcr);
            if (endTrim != -1)
            {
                Int64 span = endTrim - pcr;
                if (span < 0)
                    span += Constants.MAX_MPEG2TS_CLOCK;
                if ((span > Constants.MAX_MPEG2TS_CLOCK / 2))
                {
                    // trim end reached
                    done = true;
                }
                span /= Constants.MPEG2TS_CLOCK_RATE;
                if (span != seconds)
                {
                    seconds = span;
                    bw.ReportProgress(0, new TimeSpan(span * 10000000));
                }
            }
            else
            {
                Int64 span = inFile.EndPcr - pcr;
                if (span < 0)
                    span += Constants.MAX_MPEG2TS_CLOCK;
                span /= Constants.MPEG2TS_CLOCK_RATE;
                if (span != seconds)
                {
                    seconds = span;
                    bw.ReportProgress(0, new TimeSpan(span * 10000000));
                }
            }
        }

        public void UpdatePts(Int64 pts, ushort pid)
        {
        }
    }
}
