#nullable disable
using System;

// C# port of emu2413 v1.5.9 (YM2413 / OPLL), commit 11676f6c43af7a53a0a940f8faea57eed73a22ba.
// https://github.com/digital-sound-antiques/emu2413
// Copyright (C) 2001-2019 Mitsutaka Okazaki. MIT license, see LICENSE.txt in this directory.
//
// Port notes: slot/patch pointers became indices so that all mutable chip state lives in value-type fields and
// arrays (captured by the debugger's reflective snapshots). Only the YM2413 tone set is included; VRC7/YMF281B
// presets, stereo panning and the debug printing helpers are omitted. Integer widths follow the C original.

namespace Emu2413
{
	public struct OpllPatch
	{
		public int TL, FB, EG, ML, AR, DR, SL, RR, KR, KL, AM, PM, WS;
	}

	public struct OpllSlot
	{
		public int Number;
		/* type flags: 000000SM; M: 0 modulator 1 carrier; S: 0 normal 1 single slot mode (sd, tom, hh or cym) */
		public int Type;
		public int Patch;           /* index into Opll.patch */
		public int Output0, Output1;
		public int WaveTable;       /* 0: full sine, 1: half sine */
		public uint PgPhase;
		public uint PgOut;
		public int PgKeep;
		public int BlkFnum;         /* (block << 9) | f-number */
		public int Fnum;
		public int Blk;
		public int EgState;
		public int Volume;
		public int KeyFlag;
		public int SusFlag;
		public int Tll;
		public int Rks;
		public int EgRateH;
		public int EgRateL;
		public int EgShift;
		public uint EgOut;
		public int UpdateRequests;
	}

	public sealed partial class Opll
	{
		/* clang-format off */
		private static readonly byte[] defaultInst =
		{
			0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
			0x71, 0x61, 0x1e, 0x17, 0xd0, 0x78, 0x00, 0x17,
			0x13, 0x41, 0x1a, 0x0d, 0xd8, 0xf7, 0x23, 0x13,
			0x13, 0x01, 0x99, 0x00, 0xf2, 0xc4, 0x21, 0x23,
			0x11, 0x61, 0x0e, 0x07, 0x8d, 0x64, 0x70, 0x27,
			0x32, 0x21, 0x1e, 0x06, 0xe1, 0x76, 0x01, 0x28,
			0x31, 0x22, 0x16, 0x05, 0xe0, 0x71, 0x00, 0x18,
			0x21, 0x61, 0x1d, 0x07, 0x82, 0x81, 0x11, 0x07,
			0x33, 0x21, 0x2d, 0x13, 0xb0, 0x70, 0x00, 0x07,
			0x61, 0x61, 0x1b, 0x06, 0x64, 0x65, 0x10, 0x17,
			0x41, 0x61, 0x0b, 0x18, 0x85, 0xf0, 0x81, 0x07,
			0x33, 0x01, 0x83, 0x11, 0xea, 0xef, 0x10, 0x04,
			0x17, 0xc1, 0x24, 0x07, 0xf8, 0xf8, 0x22, 0x12,
			0x61, 0x50, 0x0c, 0x05, 0xd2, 0xf5, 0x40, 0x42,
			0x01, 0x01, 0x55, 0x03, 0xe9, 0x90, 0x03, 0x02,
			0x41, 0x41, 0x89, 0x03, 0xf1, 0xe4, 0xc0, 0x13,
			0x01, 0x01, 0x18, 0x0f, 0xdf, 0xf8, 0x6a, 0x6d,
			0x01, 0x01, 0x00, 0x00, 0xc8, 0xd8, 0xa7, 0x68,
			0x05, 0x01, 0x00, 0x00, 0xf8, 0xaa, 0x59, 0x55
		};

		/* exp_table[x] = round((exp2((double)x / 256.0) - 1) * 1024) */
		private static readonly ushort[] expTable =
		{
			0, 3, 6, 8, 11, 14, 17, 20, 22, 25, 28, 31, 34, 37, 40, 42,
			45, 48, 51, 54, 57, 60, 63, 66, 69, 72, 75, 78, 81, 84, 87, 90,
			93, 96, 99, 102, 105, 108, 111, 114, 117, 120, 123, 126, 130, 133, 136, 139,
			142, 145, 148, 152, 155, 158, 161, 164, 168, 171, 174, 177, 181, 184, 187, 190,
			194, 197, 200, 204, 207, 210, 214, 217, 220, 224, 227, 231, 234, 237, 241, 244,
			248, 251, 255, 258, 262, 265, 268, 272, 276, 279, 283, 286, 290, 293, 297, 300,
			304, 308, 311, 315, 318, 322, 326, 329, 333, 337, 340, 344, 348, 352, 355, 359,
			363, 367, 370, 374, 378, 382, 385, 389, 393, 397, 401, 405, 409, 412, 416, 420,
			424, 428, 432, 436, 440, 444, 448, 452, 456, 460, 464, 468, 472, 476, 480, 484,
			488, 492, 496, 501, 505, 509, 513, 517, 521, 526, 530, 534, 538, 542, 547, 551,
			555, 560, 564, 568, 572, 577, 581, 585, 590, 594, 599, 603, 607, 612, 616, 621,
			625, 630, 634, 639, 643, 648, 652, 657, 661, 666, 670, 675, 680, 684, 689, 693,
			698, 703, 708, 712, 717, 722, 726, 731, 736, 741, 745, 750, 755, 760, 765, 770,
			774, 779, 784, 789, 794, 799, 804, 809, 814, 819, 824, 829, 834, 839, 844, 849,
			854, 859, 864, 869, 874, 880, 885, 890, 895, 900, 906, 911, 916, 921, 927, 932,
			937, 942, 948, 953, 959, 964, 969, 975, 980, 986, 991, 996, 1002, 1007, 1013, 1018
		};

		/* fullsin_table[x] = round(-log2(sin((x + 0.5) * PI / (PG_WIDTH / 4) / 2)) * 256) */
		private static readonly ushort[] fullsinTable = MakeFullSinTable(new ushort[]
		{
			2137, 1731, 1543, 1419, 1326, 1252, 1190, 1137, 1091, 1050, 1013, 979, 949, 920, 894, 869,
			846, 825, 804, 785, 767, 749, 732, 717, 701, 687, 672, 659, 646, 633, 621, 609,
			598, 587, 576, 566, 556, 546, 536, 527, 518, 509, 501, 492, 484, 476, 468, 461,
			453, 446, 439, 432, 425, 418, 411, 405, 399, 392, 386, 380, 375, 369, 363, 358,
			352, 347, 341, 336, 331, 326, 321, 316, 311, 307, 302, 297, 293, 289, 284, 280,
			276, 271, 267, 263, 259, 255, 251, 248, 244, 240, 236, 233, 229, 226, 222, 219,
			215, 212, 209, 205, 202, 199, 196, 193, 190, 187, 184, 181, 178, 175, 172, 169,
			167, 164, 161, 159, 156, 153, 151, 148, 146, 143, 141, 138, 136, 134, 131, 129,
			127, 125, 122, 120, 118, 116, 114, 112, 110, 108, 106, 104, 102, 100, 98, 96,
			94, 92, 91, 89, 87, 85, 83, 82, 80, 78, 77, 75, 74, 72, 70, 69,
			67, 66, 64, 63, 62, 60, 59, 57, 56, 55, 53, 52, 51, 49, 48, 47,
			46, 45, 43, 42, 41, 40, 39, 38, 37, 36, 35, 34, 33, 32, 31, 30,
			29, 28, 27, 26, 25, 24, 23, 23, 22, 21, 20, 20, 19, 18, 17, 17,
			16, 15, 15, 14, 13, 13, 12, 12, 11, 10, 10, 9, 9, 8, 8, 7,
			7, 7, 6, 6, 5, 5, 5, 4, 4, 4, 3, 3, 3, 2, 2, 2,
			2, 1, 1, 1, 1, 1, 1, 1, 0, 0, 0, 0, 0, 0, 0, 0
		});

		/* amplitude lfo table, each element repeats 64 cycles */
		private static readonly byte[] amTable =
		{
			0, 0, 0, 0, 0, 0, 0, 0, 1, 1, 1, 1, 1, 1, 1, 1,
			2, 2, 2, 2, 2, 2, 2, 2, 3, 3, 3, 3, 3, 3, 3, 3,
			4, 4, 4, 4, 4, 4, 4, 4, 5, 5, 5, 5, 5, 5, 5, 5,
			6, 6, 6, 6, 6, 6, 6, 6, 7, 7, 7, 7, 7, 7, 7, 7,
			8, 8, 8, 8, 8, 8, 8, 8, 9, 9, 9, 9, 9, 9, 9, 9,
			10, 10, 10, 10, 10, 10, 10, 10, 11, 11, 11, 11, 11, 11, 11, 11,
			12, 12, 12, 12, 12, 12, 12, 12, 13, 13, 13, 12, 12, 12, 12, 12,
			12, 12, 12, 11, 11, 11, 11, 11, 11, 11, 11, 10, 10, 10, 10, 10,
			10, 10, 10, 9, 9, 9, 9, 9, 9, 9, 9, 8, 8, 8, 8, 8,
			8, 8, 8, 7, 7, 7, 7, 7, 7, 7, 7, 6, 6, 6, 6, 6,
			6, 6, 6, 5, 5, 5, 5, 5, 5, 5, 5, 4, 4, 4, 4, 4,
			4, 4, 4, 3, 3, 3, 3, 3, 3, 3, 3, 2, 2, 2, 2, 2,
			2, 2, 2, 1, 1, 1, 1, 1, 1, 1, 1, 0, 0, 0, 0, 0,
			0, 0
		};
		/* clang-format on */

		private const int OPLL_TONE_NUM = 1;

		/* phase increment counter */
		private const int PG_BITS = 10;
		private const int PG_WIDTH = 1 << PG_BITS;
		private const int DP_BITS = 19;
		private const int DP_WIDTH = 1 << DP_BITS;
		private const int DP_BASE_BITS = DP_BITS - PG_BITS;

		/* dynamic range of envelope output */
		private const double EG_STEP = 0.375;
		private const int EG_BITS = 7;
		private const int EG_MUTE = (1 << EG_BITS) - 1;
		private const int EG_MAX = EG_MUTE - 4;

		/* dynamic range of total level */
		private const int TL_BITS = 6;

		/* damper speed before key-on. key-scale affects. */
		private const int DAMPER_RATE = 12;

		private static int TL2EG(int d) => d << 1;

		private static readonly ushort[] halfsinTable = MakeHalfSinTable();
		private static readonly ushort[][] waveTableMap = { fullsinTable, halfsinTable };

		/* pitch modulator: offset to fnum, rough approximation of 14 cents depth. */
		private static readonly sbyte[,] pmTable =
		{
			{ 0, 0, 0, 0, 0, 0, 0, 0 },
			{ 0, 0, 1, 0, 0, 0, -1, 0 },
			{ 0, 1, 2, 1, 0, -1, -2, -1 },
			{ 0, 1, 3, 1, 0, -1, -3, -1 },
			{ 0, 2, 4, 2, 0, -2, -4, -2 },
			{ 0, 2, 5, 2, 0, -2, -5, -2 },
			{ 0, 3, 6, 3, 0, -3, -6, -3 },
			{ 0, 3, 7, 3, 0, -3, -7, -3 },
		};

		/* envelope decay increment step table, based on andete's research */
		private static readonly byte[,] egStepTables =
		{
			{ 0, 1, 0, 1, 0, 1, 0, 1 },
			{ 0, 1, 0, 1, 1, 1, 0, 1 },
			{ 0, 1, 1, 1, 0, 1, 1, 1 },
			{ 0, 1, 1, 1, 1, 1, 1, 1 },
		};

		private const int ATTACK = 0, DECAY = 1, SUSTAIN = 2, RELEASE = 3, DAMP = 4;

		private static readonly uint[] mlTable = { 1, 1 * 2, 2 * 2, 3 * 2, 4 * 2, 5 * 2, 6 * 2, 7 * 2,
			8 * 2, 9 * 2, 10 * 2, 10 * 2, 12 * 2, 12 * 2, 15 * 2, 15 * 2 };

		private static double DB2(double x) => x * 2;
		private static readonly double[] klTable = { DB2(0.000), DB2(9.000), DB2(12.000), DB2(13.875), DB2(15.000),
			DB2(16.125), DB2(16.875), DB2(17.625), DB2(18.000), DB2(18.750), DB2(19.125), DB2(19.500), DB2(19.875),
			DB2(20.250), DB2(20.625), DB2(21.000) };

		private static readonly int[,,] tllTable = MakeTllTable();
		private static readonly int[,] rksTable = MakeRksTable();
		private static readonly OpllPatch[] defaultPatch = MakeDefaultPatch();

		/***************************************************
		           Internal Sample Rate Converter
		****************************************************/
		/* LW is truncate length of sinc(x) calculation. SINC_RESO is the table resolution. */
		private const int LW = 16;
		private const int SINC_RESO = 256;
		private const int SINC_AMP_BITS = 12;

		private static double Blackman(double x) => 0.42 - 0.5 * Math.Cos(2 * Math.PI * x) + 0.08 * Math.Cos(4 * Math.PI * x);
		private static double Sinc(double x) => x == 0.0 ? 1.0 : Math.Sin(Math.PI * x) / (Math.PI * x);
		private static double WindowedSinc(double x) => Blackman(0.5 + 0.5 * x / (LW / 2)) * Sinc(x);

		private short[] sincTable;
		private bool convEnabled;
		private double convRatio;
		private double convTimer;
		private readonly short[] convBuffer = new short[LW];

		private void RateConvNew(double fInp, double fOut)
		{
			convEnabled = true;
			convRatio = fInp / fOut;
			sincTable = new short[SINC_RESO * LW / 2];
			for (int i = 0; i < SINC_RESO * LW / 2; i++)
			{
				double x = (double)i / SINC_RESO;
				if (fOut < fInp)
					/* for downsampling */
					sincTable[i] = (short)((1 << SINC_AMP_BITS) * WindowedSinc(x / convRatio) / convRatio);
				else
					/* for upsampling */
					sincTable[i] = (short)((1 << SINC_AMP_BITS) * WindowedSinc(x));
			}
		}

		private short LookupSincTable(double x)
		{
			short index = (short)(x * SINC_RESO);
			if (index < 0) index = (short)-index;
			return sincTable[Math.Min(SINC_RESO * LW / 2 - 1, (int)index)];
		}

		private void RateConvReset()
		{
			convTimer = 0;
			Array.Clear(convBuffer);
		}

		/* put original data to this converter at f_inp. */
		private void RateConvPutData(short data)
		{
			for (int i = 0; i < LW - 1; i++) convBuffer[i] = convBuffer[i + 1];
			convBuffer[LW - 1] = data;
		}

		/* get resampled data from this converter at f_out. */
		private short RateConvGetData()
		{
			int sum = 0;
			convTimer += convRatio;
			double dn = convTimer - Math.Floor(convTimer);
			convTimer = dn;
			for (int k = 0; k < LW; k++)
			{
				double x = ((double)k - (LW / 2 - 1)) - dn;
				sum += convBuffer[k] * LookupSincTable(x);
			}
			return (short)(sum >> SINC_AMP_BITS);
		}

		/***************************************************
		                  Create tables
		****************************************************/
		private static ushort[] MakeFullSinTable(ushort[] quarter)
		{
			var table = new ushort[PG_WIDTH];
			Array.Copy(quarter, table, PG_WIDTH / 4);
			for (int x = 0; x < PG_WIDTH / 4; x++)
				table[PG_WIDTH / 4 + x] = table[PG_WIDTH / 4 - x - 1];
			for (int x = 0; x < PG_WIDTH / 2; x++)
				table[PG_WIDTH / 2 + x] = (ushort)(0x8000 | table[x]);
			return table;
		}

		private static ushort[] MakeHalfSinTable()
		{
			var table = new ushort[PG_WIDTH];
			for (int x = 0; x < PG_WIDTH / 2; x++) table[x] = fullsinTable[x];
			for (int x = PG_WIDTH / 2; x < PG_WIDTH; x++) table[x] = 0xfff;
			return table;
		}

		private static int[,,] MakeTllTable()
		{
			var table = new int[8 * 16, 1 << TL_BITS, 4];
			for (int fnum = 0; fnum < 16; fnum++)
				for (int block = 0; block < 8; block++)
					for (int TL = 0; TL < 64; TL++)
						for (int KL = 0; KL < 4; KL++)
						{
							if (KL == 0)
							{
								table[(block << 4) | fnum, TL, KL] = TL2EG(TL);
							}
							else
							{
								int tmp = (int)(klTable[fnum] - DB2(3.000) * (7 - block));
								if (tmp <= 0)
									table[(block << 4) | fnum, TL, KL] = TL2EG(TL);
								else
									table[(block << 4) | fnum, TL, KL] = (int)(uint)((tmp >> (3 - KL)) / EG_STEP) + TL2EG(TL);
							}
						}
			return table;
		}

		private static int[,] MakeRksTable()
		{
			var table = new int[8 * 2, 2];
			for (int fnum8 = 0; fnum8 < 2; fnum8++)
				for (int block = 0; block < 8; block++)
				{
					table[(block << 1) | fnum8, 1] = (block << 1) + fnum8;
					table[(block << 1) | fnum8, 0] = block >> 1;
				}
			return table;
		}

		private static OpllPatch[] MakeDefaultPatch()
		{
			var patches = new OpllPatch[OPLL_TONE_NUM * 19 * 2];
			for (int j = 0; j < 19; j++)
				DumpToPatch(defaultInst, j * 8, patches, j * 2);
			return patches;
		}

		/*********************************************************
		                      Synthesizing
		*********************************************************/
		private const int SLOT_BD1 = 12;
		private const int SLOT_BD2 = 13;
		private const int SLOT_HH = 14;
		private const int SLOT_SD = 15;
		private const int SLOT_TOM = 16;
		private const int SLOT_CYM = 17;

		/* mask */
		public static uint MaskChannel(int x) => 1u << x;
		public const uint MASK_HH = 1 << 9;
		public const uint MASK_CYM = 1 << 10;
		public const uint MASK_TOM = 1 << 11;
		public const uint MASK_SD = 1 << 12;
		public const uint MASK_BD = 1 << 13;

		private const int NULL_PATCH = 19 * 2;

		private static int MOD(int x) => x << 1;
		private static int CAR(int x) => (x << 1) | 1;
		private static int BIT(uint s, int b) => (int)((s >> b) & 1);

		private uint clk;
		private uint rate;
		private uint adr;
		private double inpStep;
		private double outStep;
		private double outTime;
		private readonly byte[] reg = new byte[0x40];
		private int testFlag;
		private uint slotKeyStatus;
		private int rhythmMode;
		private uint egCounter;
		private uint pmPhase;
		private int amPhase;
		private int lfoAm;
		private uint noise;
		private int shortNoise;
		private readonly int[] patchNumber = new int[9];
		private readonly OpllSlot[] slot = new OpllSlot[18];
		/* 19 * 2 patches plus the null patch used before key assignment */
		private readonly OpllPatch[] patch = new OpllPatch[19 * 2 + 1];
		private uint mask;
		/* channel output: 0..8 tone 9 bd 10 hh 11 sd 12 tom 13 cym */
		private readonly short[] chOut = new short[14];
		private short mixOut;

		private int GetParameterRate(ref OpllSlot s)
		{
			if ((s.Type & 1) == 0 && s.KeyFlag == 0) return 0;

			ref var p = ref patch[s.Patch];
			switch (s.EgState)
			{
				case ATTACK: return p.AR;
				case DECAY: return p.DR;
				case SUSTAIN: return p.EG != 0 ? 0 : p.RR;
				case RELEASE:
					if (s.SusFlag != 0) return 5;
					else if (p.EG != 0) return p.RR;
					else return 7;
				case DAMP: return DAMPER_RATE;
				default: return 0;
			}
		}

		private const int UPDATE_WS = 1, UPDATE_TLL = 2, UPDATE_RKS = 4, UPDATE_EG = 8, UPDATE_ALL = 255;

		private static void RequestUpdate(ref OpllSlot s, int flag) => s.UpdateRequests |= flag;

		private void CommitSlotUpdate(ref OpllSlot s)
		{
			ref var p = ref patch[s.Patch];
			if ((s.UpdateRequests & UPDATE_WS) != 0)
				s.WaveTable = p.WS;

			if ((s.UpdateRequests & UPDATE_TLL) != 0)
			{
				if ((s.Type & 1) == 0)
					s.Tll = tllTable[s.BlkFnum >> 5, p.TL, p.KL];
				else
					s.Tll = tllTable[s.BlkFnum >> 5, s.Volume, p.KL];
			}

			if ((s.UpdateRequests & UPDATE_RKS) != 0)
				s.Rks = rksTable[s.BlkFnum >> 8, p.KR];

			if ((s.UpdateRequests & (UPDATE_RKS | UPDATE_EG)) != 0)
			{
				int pRate = GetParameterRate(ref s);

				if (pRate == 0)
				{
					s.EgShift = 0;
					s.EgRateH = 0;
					s.EgRateL = 0;
					/* As in the original, pending requests are kept when the rate is zero. */
					return;
				}

				s.EgRateH = Math.Min(15, pRate + (s.Rks >> 2));
				s.EgRateL = s.Rks & 3;
				if (s.EgState == ATTACK)
					s.EgShift = (0 < s.EgRateH && s.EgRateH < 12) ? (13 - s.EgRateH) : 0;
				else
					s.EgShift = (s.EgRateH < 13) ? (13 - s.EgRateH) : 0;
			}

			s.UpdateRequests = 0;
		}

		private static void ResetSlot(ref OpllSlot s, int number)
		{
			s.Number = number;
			s.Type = number % 2;
			s.PgKeep = 0;
			s.WaveTable = 0;
			s.PgPhase = 0;
			s.Output0 = 0;
			s.Output1 = 0;
			s.EgState = RELEASE;
			s.EgShift = 0;
			s.Rks = 0;
			s.Tll = 0;
			s.KeyFlag = 0;
			s.SusFlag = 0;
			s.BlkFnum = 0;
			s.Blk = 0;
			s.Fnum = 0;
			s.Volume = 0;
			s.PgOut = 0;
			s.EgOut = EG_MUTE;
			s.Patch = NULL_PATCH;
			s.UpdateRequests = 0;
		}

		private void SlotOn(int i)
		{
			ref var s = ref slot[i];
			s.KeyFlag = 1;
			s.EgState = DAMP;
			RequestUpdate(ref s, UPDATE_EG);
		}

		private void SlotOff(int i)
		{
			ref var s = ref slot[i];
			s.KeyFlag = 0;
			if ((s.Type & 1) != 0)
			{
				s.EgState = RELEASE;
				RequestUpdate(ref s, UPDATE_EG);
			}
		}

		private void UpdateKeyStatus()
		{
			byte r14 = reg[0x0e];
			int rhythm = BIT(r14, 5);
			uint newSlotKeyStatus = 0;

			for (int ch = 0; ch < 9; ch++)
				if ((reg[0x20 + ch] & 0x10) != 0)
					newSlotKeyStatus |= 3u << (ch * 2);

			if (rhythm != 0)
			{
				if ((r14 & 0x10) != 0) newSlotKeyStatus |= 3u << SLOT_BD1;
				if ((r14 & 0x01) != 0) newSlotKeyStatus |= 1u << SLOT_HH;
				if ((r14 & 0x08) != 0) newSlotKeyStatus |= 1u << SLOT_SD;
				if ((r14 & 0x04) != 0) newSlotKeyStatus |= 1u << SLOT_TOM;
				if ((r14 & 0x02) != 0) newSlotKeyStatus |= 1u << SLOT_CYM;
			}

			uint updatedStatus = slotKeyStatus ^ newSlotKeyStatus;

			if (updatedStatus != 0)
			{
				for (int i = 0; i < 18; i++)
					if (BIT(updatedStatus, i) != 0)
					{
						if (BIT(newSlotKeyStatus, i) != 0) SlotOn(i);
						else SlotOff(i);
					}
			}

			slotKeyStatus = newSlotKeyStatus;
		}

		private void SetPatch(int ch, int num)
		{
			patchNumber[ch] = num;
			slot[MOD(ch)].Patch = num * 2 + 0;
			slot[CAR(ch)].Patch = num * 2 + 1;
			RequestUpdate(ref slot[MOD(ch)], UPDATE_ALL);
			RequestUpdate(ref slot[CAR(ch)], UPDATE_ALL);
		}

		private void SetSusFlag(int ch, int flag)
		{
			slot[CAR(ch)].SusFlag = flag;
			RequestUpdate(ref slot[CAR(ch)], UPDATE_EG);
			if ((slot[MOD(ch)].Type & 1) != 0)
			{
				slot[MOD(ch)].SusFlag = flag;
				RequestUpdate(ref slot[MOD(ch)], UPDATE_EG);
			}
		}

		/* set volume ( volume : 6bit, register value << 2 ) */
		private void SetVolume(int ch, int volume)
		{
			slot[CAR(ch)].Volume = volume;
			RequestUpdate(ref slot[CAR(ch)], UPDATE_TLL);
		}

		private static void SetSlotVolume(ref OpllSlot s, int volume)
		{
			s.Volume = volume;
			RequestUpdate(ref s, UPDATE_TLL);
		}

		/* set f-Nnmber ( fnum : 9bit ) */
		private void SetFnumber(int ch, int fnum)
		{
			ref var car = ref slot[CAR(ch)];
			ref var mod = ref slot[MOD(ch)];
			car.Fnum = fnum;
			car.BlkFnum = (car.BlkFnum & 0xe00) | (fnum & 0x1ff);
			mod.Fnum = fnum;
			mod.BlkFnum = (mod.BlkFnum & 0xe00) | (fnum & 0x1ff);
			RequestUpdate(ref car, UPDATE_EG | UPDATE_RKS | UPDATE_TLL);
			RequestUpdate(ref mod, UPDATE_EG | UPDATE_RKS | UPDATE_TLL);
		}

		/* set block data (blk : 3bit ) */
		private void SetBlock(int ch, int blk)
		{
			ref var car = ref slot[CAR(ch)];
			ref var mod = ref slot[MOD(ch)];
			car.Blk = blk;
			car.BlkFnum = ((blk & 7) << 9) | (car.BlkFnum & 0x1ff);
			mod.Blk = blk;
			mod.BlkFnum = ((blk & 7) << 9) | (mod.BlkFnum & 0x1ff);
			RequestUpdate(ref car, UPDATE_EG | UPDATE_RKS | UPDATE_TLL);
			RequestUpdate(ref mod, UPDATE_EG | UPDATE_RKS | UPDATE_TLL);
		}

		private void UpdateRhythmMode()
		{
			int newRhythmMode = (reg[0x0e] >> 5) & 1;

			if (rhythmMode != newRhythmMode)
			{
				if (newRhythmMode != 0)
				{
					slot[SLOT_HH].Type = 3;
					slot[SLOT_HH].PgKeep = 1;
					slot[SLOT_SD].Type = 3;
					slot[SLOT_TOM].Type = 3;
					slot[SLOT_CYM].Type = 3;
					slot[SLOT_CYM].PgKeep = 1;
					SetPatch(6, 16);
					SetPatch(7, 17);
					SetPatch(8, 18);
					SetSlotVolume(ref slot[SLOT_HH], ((reg[0x37] >> 4) & 15) << 2);
					SetSlotVolume(ref slot[SLOT_TOM], ((reg[0x38] >> 4) & 15) << 2);
				}
				else
				{
					slot[SLOT_HH].Type = 0;
					slot[SLOT_HH].PgKeep = 0;
					slot[SLOT_SD].Type = 1;
					slot[SLOT_TOM].Type = 0;
					slot[SLOT_CYM].Type = 1;
					slot[SLOT_CYM].PgKeep = 0;
					SetPatch(6, reg[0x36] >> 4);
					SetPatch(7, reg[0x37] >> 4);
					SetPatch(8, reg[0x38] >> 4);
				}
			}

			rhythmMode = newRhythmMode;
		}

		private void UpdateAmPm()
		{
			if ((testFlag & 2) != 0)
			{
				pmPhase = 0;
				amPhase = 0;
			}
			else
			{
				pmPhase += (testFlag & 8) != 0 ? 1024u : 1u;
				amPhase += (testFlag & 8) != 0 ? 64 : 1;
			}
			lfoAm = amTable[(amPhase >> 6) % amTable.Length];
		}

		private void UpdateNoise(int cycle)
		{
			for (int i = 0; i < cycle; i++)
			{
				if ((noise & 1) != 0) noise ^= 0x800200;
				noise >>= 1;
			}
		}

		private void UpdateShortNoise()
		{
			uint pgHh = slot[SLOT_HH].PgOut;
			uint pgCym = slot[SLOT_CYM].PgOut;

			int hBit2 = BIT(pgHh, PG_BITS - 8);
			int hBit7 = BIT(pgHh, PG_BITS - 3);
			int hBit3 = BIT(pgHh, PG_BITS - 7);

			int cBit3 = BIT(pgCym, PG_BITS - 7);
			int cBit5 = BIT(pgCym, PG_BITS - 5);

			shortNoise = (hBit2 ^ hBit7) | (hBit3 ^ cBit5) | (cBit3 ^ cBit5);
		}

		private void CalcPhase(ref OpllSlot s, uint pm_phase, bool reset)
		{
			ref var p = ref patch[s.Patch];
			int pm = p.PM != 0 ? pmTable[(s.Fnum >> 6) & 7, (int)((pm_phase >> 10) & 7)] : 0;
			if (reset) s.PgPhase = 0;
			/* unsigned arithmetic, as in C (int * uint32_t) */
			uint increment = unchecked((uint)((s.Fnum & 0x1ff) * 2 + pm) * mlTable[p.ML]);
			s.PgPhase += (increment << s.Blk) >> 2;
			s.PgPhase &= DP_WIDTH - 1;
			s.PgOut = s.PgPhase >> DP_BASE_BITS;
		}

		private static int LookupAttackStep(ref OpllSlot s, uint counter)
		{
			int index;
			switch (s.EgRateH)
			{
				case 12:
					index = (int)((counter & 0xc) >> 1);
					return 4 - egStepTables[s.EgRateL, index];
				case 13:
					index = (int)((counter & 0xc) >> 1);
					return 3 - egStepTables[s.EgRateL, index];
				case 14:
					index = (int)((counter & 0xc) >> 1);
					return 2 - egStepTables[s.EgRateL, index];
				case 0:
				case 15:
					return 0;
				default:
					index = (int)(counter >> s.EgShift);
					return egStepTables[s.EgRateL, index & 7] != 0 ? 4 : 0;
			}
		}

		private static int LookupDecayStep(ref OpllSlot s, uint counter)
		{
			int index;
			switch (s.EgRateH)
			{
				case 0:
					return 0;
				case 13:
					index = (int)(((counter & 0xc) >> 1) | (counter & 1));
					return egStepTables[s.EgRateL, index];
				case 14:
					index = (int)((counter & 0xc) >> 1);
					return egStepTables[s.EgRateL, index] + 1;
				case 15:
					return 2;
				default:
					index = (int)(counter >> s.EgShift);
					return egStepTables[s.EgRateL, index & 7];
			}
		}

		private void StartEnvelope(ref OpllSlot s)
		{
			if (Math.Min(15, patch[s.Patch].AR + (s.Rks >> 2)) == 15)
			{
				s.EgState = DECAY;
				s.EgOut = 0;
			}
			else
			{
				s.EgState = ATTACK;
			}
			RequestUpdate(ref s, UPDATE_EG);
		}

		private void CalcEnvelope(int index, int buddy, ushort counter16, bool test)
		{
			ref var s = ref slot[index];
			uint counter = counter16;
			uint egMask = (1u << s.EgShift) - 1;

			if (s.EgState == ATTACK)
			{
				if (0 < s.EgOut && 0 < s.EgRateH && (counter & egMask & ~3u) == 0)
				{
					int step = LookupAttackStep(ref s, counter);
					if (0 < step)
						s.EgOut = (uint)Math.Max(0, (int)s.EgOut - (int)(s.EgOut >> step) - 1);
				}
			}
			else
			{
				if (s.EgRateH > 0 && (counter & egMask) == 0)
					s.EgOut = (uint)Math.Min(EG_MUTE, (int)s.EgOut + LookupDecayStep(ref s, counter));
			}

			switch (s.EgState)
			{
				case DAMP:
					// DAMP to ATTACK transition is occured when the envelope reaches EG_MAX (max attenuation but it's not mute).
					// Do not forget to check (eg_counter & mask) == 0 to synchronize it with the progress of the envelope.
					if (s.EgOut >= EG_MAX && (counter & egMask) == 0)
					{
						StartEnvelope(ref s);
						if ((s.Type & 1) != 0)
						{
							if (s.PgKeep == 0) s.PgPhase = 0;
							if (buddy >= 0 && slot[buddy].PgKeep == 0) slot[buddy].PgPhase = 0;
						}
					}
					break;

				case ATTACK:
					if (s.EgOut == 0)
					{
						s.EgState = DECAY;
						RequestUpdate(ref s, UPDATE_EG);
					}
					break;

				case DECAY:
					// DECAY to SUSTAIN transition must be checked at every cycle regardless of the conditions of the envelope rate and
					// counter. i.e. the transition is not synchronized with the progress of the envelope.
					if ((s.EgOut >> 3) == patch[s.Patch].SL)
					{
						s.EgState = SUSTAIN;
						RequestUpdate(ref s, UPDATE_EG);
					}
					break;
			}

			if (test) s.EgOut = 0;
		}

		private void UpdateSlots()
		{
			egCounter++;

			for (int i = 0; i < 18; i++)
			{
				ref var s = ref slot[i];
				int buddy = -1;
				if (s.Type == 0) buddy = i + 1;
				if (s.Type == 1) buddy = i - 1;
				if (s.UpdateRequests != 0) CommitSlotUpdate(ref s);
				CalcEnvelope(i, buddy, (ushort)egCounter, (testFlag & 1) != 0);
				CalcPhase(ref s, pmPhase, (testFlag & 4) != 0);
			}
		}

		/* output: -4095...4095 */
		private static short LookupExpTable(ushort i)
		{
			/* from andete's expression */
			short t = (short)(expTable[(i & 0xff) ^ 0xff] + 1024);
			short res = (short)(t >> ((i & 0x7f00) >> 8));
			return (short)(((i & 0x8000) != 0 ? ~res : res) << 1);
		}

		private static short ToLinear(ushort h, ref OpllSlot s, int am)
		{
			if (s.EgOut > EG_MAX) return 0;

			int att = Math.Min(EG_MUTE, (int)s.EgOut + s.Tll + am) << 4;
			return LookupExpTable((ushort)(h + att));
		}

		private short CalcSlotCar(int ch, short fm)
		{
			ref var s = ref slot[CAR(ch)];
			int am = patch[s.Patch].AM != 0 ? lfoAm : 0;

			s.Output1 = s.Output0;
			s.Output0 = ToLinear(waveTableMap[s.WaveTable][((int)s.PgOut + 2 * (fm >> 1)) & (PG_WIDTH - 1)], ref s, am);

			return (short)s.Output0;
		}

		private short CalcSlotMod(int ch)
		{
			ref var s = ref slot[MOD(ch)];
			ref var p = ref patch[s.Patch];

			short fm = p.FB > 0 ? (short)((s.Output1 + s.Output0) >> (9 - p.FB)) : (short)0;
			int am = p.AM != 0 ? lfoAm : 0;

			s.Output1 = s.Output0;
			s.Output0 = ToLinear(waveTableMap[s.WaveTable][((int)s.PgOut + fm) & (PG_WIDTH - 1)], ref s, am);

			return (short)s.Output0;
		}

		private short CalcSlotTom()
		{
			ref var s = ref slot[MOD(8)];
			return ToLinear(waveTableMap[s.WaveTable][s.PgOut], ref s, 0);
		}

		private short CalcSlotSnare()
		{
			ref var s = ref slot[CAR(7)];
			int phase;
			if (BIT(s.PgOut, PG_BITS - 2) != 0)
				phase = (noise & 1) != 0 ? 0x300 : 0x200;
			else
				phase = (noise & 1) != 0 ? 0x0 : 0x100;
			return ToLinear(waveTableMap[s.WaveTable][phase], ref s, 0);
		}

		private short CalcSlotCym()
		{
			ref var s = ref slot[CAR(8)];
			int phase = shortNoise != 0 ? 0x300 : 0x100;
			return ToLinear(waveTableMap[s.WaveTable][phase], ref s, 0);
		}

		private short CalcSlotHat()
		{
			ref var s = ref slot[MOD(7)];
			int phase;
			if (shortNoise != 0)
				phase = (noise & 1) != 0 ? 0x2d0 : 0x234;
			else
				phase = (noise & 1) != 0 ? 0x34 : 0xd0;
			return ToLinear(waveTableMap[s.WaveTable][phase], ref s, 0);
		}

		private static short MO(short x) => (short)(-x >> 1);
		private static short RO(short x) => x;

		private void UpdateOutput()
		{
			UpdateAmPm();
			UpdateShortNoise();
			UpdateSlots();

			/* CH1-6 */
			for (int i = 0; i < 6; i++)
				if ((mask & MaskChannel(i)) == 0)
					chOut[i] = MO(CalcSlotCar(i, CalcSlotMod(i)));

			/* CH7 */
			if (rhythmMode == 0)
			{
				if ((mask & MaskChannel(6)) == 0)
					chOut[6] = MO(CalcSlotCar(6, CalcSlotMod(6)));
			}
			else
			{
				if ((mask & MASK_BD) == 0)
					chOut[9] = RO(CalcSlotCar(6, CalcSlotMod(6)));
			}
			UpdateNoise(14);

			/* CH8 */
			if (rhythmMode == 0)
			{
				if ((mask & MaskChannel(7)) == 0)
					chOut[7] = MO(CalcSlotCar(7, CalcSlotMod(7)));
			}
			else
			{
				if ((mask & MASK_HH) == 0)
					chOut[10] = RO(CalcSlotHat());
				if ((mask & MASK_SD) == 0)
					chOut[11] = RO(CalcSlotSnare());
			}
			UpdateNoise(2);

			/* CH9 */
			if (rhythmMode == 0)
			{
				if ((mask & MaskChannel(8)) == 0)
					chOut[8] = MO(CalcSlotCar(8, CalcSlotMod(8)));
			}
			else
			{
				if ((mask & MASK_TOM) == 0)
					chOut[12] = RO(CalcSlotTom());
				if ((mask & MASK_CYM) == 0)
					chOut[13] = RO(CalcSlotCym());
			}
			UpdateNoise(2);
		}

		private void MixOutput()
		{
			short output = 0;
			for (int i = 0; i < 14; i++) output = unchecked((short)(output + chOut[i]));
			if (convEnabled) RateConvPutData(output);
			else mixOut = output;
		}

		/***********************************************************
		                   External Interfaces
		***********************************************************/
		public Opll(uint clock, uint sampleRate)
		{
			for (int i = 0; i < patch.Length; i++) patch[i] = default;

			clk = clock;
			rate = sampleRate;
			mask = 0;
			mixOut = 0;

			Reset();
			ResetPatch();
		}

		private void ResetRateConversionParams()
		{
			double fOut = rate;
			double fInp = clk / 72.0;

			outTime = 0;
			outStep = fInp;
			inpStep = fOut;

			convEnabled = false;
			if (Math.Floor(fInp) != fOut && Math.Floor(fInp + 0.5) != fOut)
				RateConvNew(fInp, fOut);

			if (convEnabled) RateConvReset();
		}

		public void Reset()
		{
			adr = 0;

			pmPhase = 0;
			amPhase = 0;

			noise = 0x1;
			mask = 0;

			rhythmMode = 0;
			slotKeyStatus = 0;
			egCounter = 0;

			ResetRateConversionParams();

			for (int i = 0; i < 18; i++) ResetSlot(ref slot[i], i);

			for (int i = 0; i < 9; i++) SetPatch(i, 0);

			for (int i = 0; i < 0x40; i++) WriteReg(i, 0);

			Array.Clear(chOut);
		}

		public void WriteReg(int register, byte data)
		{
			if (register >= 0x40) return;

			/* mirror registers */
			if ((0x19 <= register && register <= 0x1f) || (0x29 <= register && register <= 0x2f) || (0x39 <= register && register <= 0x3f))
				register -= 9;

			reg[register] = data;

			switch (register)
			{
				case 0x00:
					patch[0].AM = (data >> 7) & 1;
					patch[0].PM = (data >> 6) & 1;
					patch[0].EG = (data >> 5) & 1;
					patch[0].KR = (data >> 4) & 1;
					patch[0].ML = data & 15;
					for (int i = 0; i < 9; i++)
						if (patchNumber[i] == 0) RequestUpdate(ref slot[MOD(i)], UPDATE_RKS | UPDATE_EG);
					break;

				case 0x01:
					patch[1].AM = (data >> 7) & 1;
					patch[1].PM = (data >> 6) & 1;
					patch[1].EG = (data >> 5) & 1;
					patch[1].KR = (data >> 4) & 1;
					patch[1].ML = data & 15;
					for (int i = 0; i < 9; i++)
						if (patchNumber[i] == 0) RequestUpdate(ref slot[CAR(i)], UPDATE_RKS | UPDATE_EG);
					break;

				case 0x02:
					patch[0].KL = (data >> 6) & 3;
					patch[0].TL = data & 63;
					for (int i = 0; i < 9; i++)
						if (patchNumber[i] == 0) RequestUpdate(ref slot[MOD(i)], UPDATE_TLL);
					break;

				case 0x03:
					patch[1].KL = (data >> 6) & 3;
					patch[1].WS = (data >> 4) & 1;
					patch[0].WS = (data >> 3) & 1;
					patch[0].FB = data & 7;
					for (int i = 0; i < 9; i++)
						if (patchNumber[i] == 0)
						{
							RequestUpdate(ref slot[MOD(i)], UPDATE_WS);
							RequestUpdate(ref slot[CAR(i)], UPDATE_WS | UPDATE_TLL);
						}
					break;

				case 0x04:
					patch[0].AR = (data >> 4) & 15;
					patch[0].DR = data & 15;
					for (int i = 0; i < 9; i++)
						if (patchNumber[i] == 0) RequestUpdate(ref slot[MOD(i)], UPDATE_EG);
					break;

				case 0x05:
					patch[1].AR = (data >> 4) & 15;
					patch[1].DR = data & 15;
					for (int i = 0; i < 9; i++)
						if (patchNumber[i] == 0) RequestUpdate(ref slot[CAR(i)], UPDATE_EG);
					break;

				case 0x06:
					patch[0].SL = (data >> 4) & 15;
					patch[0].RR = data & 15;
					for (int i = 0; i < 9; i++)
						if (patchNumber[i] == 0) RequestUpdate(ref slot[MOD(i)], UPDATE_EG);
					break;

				case 0x07:
					patch[1].SL = (data >> 4) & 15;
					patch[1].RR = data & 15;
					for (int i = 0; i < 9; i++)
						if (patchNumber[i] == 0) RequestUpdate(ref slot[CAR(i)], UPDATE_EG);
					break;

				case 0x0e:
					UpdateRhythmMode();
					UpdateKeyStatus();
					break;

				case 0x0f:
					testFlag = data;
					break;

				case >= 0x10 and <= 0x18:
				{
					int ch = register - 0x10;
					SetFnumber(ch, data + ((reg[0x20 + ch] & 1) << 8));
					break;
				}

				case >= 0x20 and <= 0x28:
				{
					int ch = register - 0x20;
					SetFnumber(ch, ((data & 1) << 8) + reg[0x10 + ch]);
					SetBlock(ch, (data >> 1) & 7);
					SetSusFlag(ch, (data >> 5) & 1);
					UpdateKeyStatus();
					break;
				}

				case >= 0x30 and <= 0x38:
					if ((reg[0x0e] & 32) != 0 && register >= 0x36)
					{
						switch (register)
						{
							case 0x37: SetSlotVolume(ref slot[MOD(7)], ((data >> 4) & 15) << 2); break;
							case 0x38: SetSlotVolume(ref slot[MOD(8)], ((data >> 4) & 15) << 2); break;
						}
					}
					else
					{
						SetPatch(register - 0x30, (data >> 4) & 15);
					}
					SetVolume(register - 0x30, (data & 15) << 2);
					break;
			}
		}

		public void WriteIO(int address, byte value)
		{
			if ((address & 1) != 0) WriteReg((int)adr, value);
			else adr = value;
		}

		private static void DumpToPatch(byte[] dump, int offset, OpllPatch[] patches, int index)
		{
			ref var p0 = ref patches[index];
			ref var p1 = ref patches[index + 1];
			p0.AM = (dump[offset + 0] >> 7) & 1;
			p1.AM = (dump[offset + 1] >> 7) & 1;
			p0.PM = (dump[offset + 0] >> 6) & 1;
			p1.PM = (dump[offset + 1] >> 6) & 1;
			p0.EG = (dump[offset + 0] >> 5) & 1;
			p1.EG = (dump[offset + 1] >> 5) & 1;
			p0.KR = (dump[offset + 0] >> 4) & 1;
			p1.KR = (dump[offset + 1] >> 4) & 1;
			p0.ML = dump[offset + 0] & 15;
			p1.ML = dump[offset + 1] & 15;
			p0.KL = (dump[offset + 2] >> 6) & 3;
			p1.KL = (dump[offset + 3] >> 6) & 3;
			p0.TL = dump[offset + 2] & 63;
			p1.TL = 0;
			p0.FB = dump[offset + 3] & 7;
			p1.FB = 0;
			p0.WS = (dump[offset + 3] >> 3) & 1;
			p1.WS = (dump[offset + 3] >> 4) & 1;
			p0.AR = (dump[offset + 4] >> 4) & 15;
			p1.AR = (dump[offset + 5] >> 4) & 15;
			p0.DR = dump[offset + 4] & 15;
			p1.DR = dump[offset + 5] & 15;
			p0.SL = (dump[offset + 6] >> 4) & 15;
			p1.SL = (dump[offset + 7] >> 4) & 15;
			p0.RR = dump[offset + 6] & 15;
			p1.RR = dump[offset + 7] & 15;
		}

		public void ResetPatch()
		{
			for (int i = 0; i < 19 * 2; i++) patch[i] = defaultPatch[i];
		}

		/* Calculate one sample at the configured output rate. */
		public short Calc()
		{
			while (outStep > outTime)
			{
				outTime += inpStep;
				UpdateOutput();
				MixOutput();
			}
			outTime -= outStep;
			if (convEnabled) mixOut = RateConvGetData();
			return mixOut;
		}

		/* Set channel mask: bit 0..8 ch 1 to 9, bit 9 HH, 10 CYM, 11 TOM, 12 SD, 13 BD. Returns the previous mask. */
		public uint SetMask(uint newMask)
		{
			uint previous = mask;
			mask = newMask;
			return previous;
		}
	}
}
