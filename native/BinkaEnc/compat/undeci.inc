/* Prism - replacement for RAD's undeci.inc
 *
 * The include provides the quantization step table ("undecibel" -> linear) the
 * encoder indexes with the per-band quality level. The decoder multiplies the
 * integer coefficients back with exactly these values
 * (reference/binka_decoder-main/binka_data.h, binka_scalefactors), i.e. they are
 * simply 10^(0.0664 * i). Copied over verbatim so both sides agree bit for bit.
 */

#ifndef __UNDECIINC__
#define __UNDECIINC__

#define BINK_UNDECIBEL_LEVELS 96

static const AUDIOFLOAT bink_Undecibel_table[BINK_UNDECIBEL_LEVELS] =
{
       1.0f,        1.1651987f,     1.3576881f,     1.5819764f,
       1.8433169f,  2.1478305f,     2.5026493f,     2.9160838f,
       3.3978171f,  3.9591322f,     4.6131759f,     5.3752666f,
       6.2632537f,  7.297935f,      8.5035448f,     9.9083195f,
      11.545161f,  13.452407f,     15.674727f,     18.264172f,
      21.281391f,  24.797049f,     28.89349f,      33.666656f,
      39.228348f,  45.70882f,      53.259857f,     62.058319f,
      72.310272f,  84.255836f,     98.174797f,    114.39314f,
     133.29074f,  155.31021f,     180.96725f,    210.86281f,
     245.69708f,  286.28592f,     333.57999f,    388.68698f,
     452.89758f,  527.7157f,      614.89362f,    716.47327f,
     834.83374f,  972.74725f,    1133.4438f,    1320.6873f,
    1538.8632f,  1793.0814f,    2089.2961f,    2434.4451f,
    2836.6123f,  3305.2173f,    3851.2349f,    4487.4541f,
    5228.7754f,  6092.5625f,    7099.0464f,    8271.7998f,
    9638.29f,   11230.523f,    13085.792f,    15247.548f,
   17766.424f,  20701.414f,    24121.26f,     28106.062f,
   32749.148f,  38159.266f,    44463.125f,    51808.379f,
   60367.055f,  70339.617f,    81959.633f,    95499.258f,
  111275.62f,  129658.2f,     151077.58f,    176035.39f,
  205116.22f,  239001.16f,    278483.84f,    324489.03f,
  378094.19f,  440554.88f,    513333.97f,    598136.06f,
  696947.38f,  812082.19f,    946237.19f,   1102554.4f,
 1284694.9f,   1496924.9f,   1744214.9f,   2032357.0f,
};

#endif
