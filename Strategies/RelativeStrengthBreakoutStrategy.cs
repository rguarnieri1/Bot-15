using BotCripto.Models;

namespace BotCripto.Strategies;

// Breakout su forza relativa (candele chiuse, timeframe 1d), long e short speculari.
// La direzione la decide il regime di mercato: SPY rialzista -> solo long, SPY ribassista -> solo short.
//
// LONG
//  0) regime: SPY sopra la sua EMA200 e con EMA200 in salita
//  1) trend rialzista: Close > EMA50 e EMA20 > EMA50 con EMA50 in salita
//  2) forza relativa: la linea RS (Close asset / Close SPY) è sopra la sua media a RsSmaPeriod
//     e ha toccato un nuovo massimo a RsHighLookback candele nelle ultime RsRecentBars candele
//  3) breakout: la candela rialzista chiude sopra il massimo delle precedenti BreakoutLookback candele,
//     con volume superiore alla media e RSI non ipercomprato
//  Stop loss sotto il minimo del range di breakout (più un buffer ATR).
// SHORT (specchio)
//  0) SPY sotto la sua EMA200 e con EMA200 in discesa
//  1) trend ribassista: Close < EMA50 e EMA20 < EMA50 con EMA50 in discesa
//  2) debolezza relativa: RS sotto la sua media e a un nuovo minimo recente
//  3) breakout: candela ribassista che chiude sotto il minimo delle precedenti BreakoutLookback candele,
//     con volume superiore alla media e RSI non ipervenduto
//  Stop loss sopra il massimo del range di breakout (più un buffer ATR).
// Il target lo calcola il RiskManager (R:R configurato).
public class RelativeStrengthBreakoutStrategy
{
    public const string Name = "Breakout Forza Relativa";

    private const int FastEma = 20;
    private const int SlowEma = 50;
    private const int BreakoutLookback = 20;     // massimo/minimo delle ultime N candele da superare
    private const int RsSmaPeriod = 20;
    private const int RsHighLookback = 30;       // la RS deve essere al massimo/minimo di questa finestra...
    private const int RsRecentBars = 10;         // ...in almeno una delle ultime candele
    private const int VolumePeriod = 20;
    private const decimal VolumeFactor = 1.5m;   // volume breakout >= 1,5 x la media
    private const int AtrPeriod = 14;
    private const decimal AtrStopBuffer = 2.5m;  // buffer oltre il range, in ATR
    private const decimal RsiMax = 80m;          // long: niente ingressi con RSI oltre questo valore
    private const decimal RsiMin = 20m;          // short: niente ingressi con RSI sotto questo valore
    private const decimal MaxStopPercent = 0.15m; // stop più largo del 15% = setup scartato
    private const int RegimeEma = 200;           // SPY vs la sua EMA (servono >= RegimeEma + 6 candele di SPY)
    private const decimal MinClosePosition = 0.8m; // la chiusura deve stare nel 20% estremo del range della candela
    public const int MinCandles = SlowEma + 20;

    // Regime di mercato: true = SPY sopra EMA200 in salita, false = SPY sotto EMA200 in discesa,
    // null = neutro (es. SPY sopra la EMA ma EMA in discesa) o dati insufficienti.
    public static bool? IsMarketBullish(List<Candle> marketCandles)
    {
        var ema = TechnicalIndicators.CalculateEMA(marketCandles.Select(c => c.Close).ToList(), RegimeEma);
        if (ema.Count < 6)
            return null;
        var close = marketCandles[^1].Close;
        if (close > ema[^1] && ema[^1] > ema[^6]) return true;
        if (close < ema[^1] && ema[^1] < ema[^6]) return false;
        return null;
    }

    public AnalysisResult Analyze(string symbol, List<Candle> candles, List<Candle>? benchmark = null)
    {
        var result = new AnalysisResult
        {
            Symbol = symbol,
            StrategyName = Name,
            IsSignal = false,
            Signal = "No Signal",
            AnalysisTime = DateTime.UtcNow
        };

        if (candles.Count < MinCandles || benchmark == null || benchmark.Count == 0)
        {
            result.Rejection = RejectionReason.InsufficientData;
            return result;
        }

        var rs = BuildRelativeStrength(candles, benchmark);
        var closes = candles.Select(c => c.Close).ToList();
        var ema20 = TechnicalIndicators.CalculateEMA(closes, FastEma);
        var ema50 = TechnicalIndicators.CalculateEMA(closes, SlowEma);
        var rsi = TechnicalIndicators.CalculateRSI(closes);
        if (rs.Count < RsHighLookback + RsRecentBars || ema20.Count < 1 || ema50.Count < 6 || rsi.Count < 1)
        {
            result.Rejection = RejectionReason.InsufficientData;
            return result;
        }

        var last = candles.Last();
        result.CurrentPrice = last.Close;

        var e20 = ema20[^1];
        var e50 = ema50[^1];
        var rsiNow = rsi[^1];
        result.Indicators["EMA20"] = e20;
        result.Indicators["EMA50"] = e50;
        result.Indicators["RSI"] = rsiNow;

        // 0) Regime di mercato: decide la direzione
        var regime = IsMarketBullish(benchmark);
        if (regime == null)
        {
            result.Rejection = RejectionReason.AgainstMarketRegime;
            return result;
        }
        bool isLong = regime.Value;

        // 1) Trend di fondo nella direzione del regime
        bool trendOk = isLong
            ? last.Close > e50 && e20 > e50 && e50 > ema50[^6]
            : last.Close < e50 && e20 < e50 && e50 < ema50[^6];
        if (!trendOk)
        {
            result.Rejection = RejectionReason.TrendNotAligned;
            return result;
        }

        // 2) Forza (debolezza) relativa: oltre la sua media e a nuovi massimi (minimi) di recente
        var rsNow = rs[^1];
        var rsSma = rs.Skip(rs.Count - RsSmaPeriod).Average();
        bool rsExtreme = false;
        for (int back = 0; back < RsRecentBars; back++)
        {
            var idx = rs.Count - 1 - back;
            var window = rs.Skip(idx + 1 - RsHighLookback).Take(RsHighLookback).ToList();
            if (isLong ? rs[idx] >= window.Max() : rs[idx] <= window.Min()) { rsExtreme = true; break; }
        }
        result.Indicators["RS"] = rsNow;
        if (!(isLong ? rsNow > rsSma : rsNow < rsSma) || !rsExtreme)
        {
            result.Rejection = RejectionReason.PriceVsEma10;
            return result;
        }

        // 3) Breakout sul massimo (minimo) delle precedenti BreakoutLookback candele (escluse l'ultima)
        var range = candles.Skip(candles.Count - 1 - BreakoutLookback).Take(BreakoutLookback).ToList();
        var rangeHigh = range.Max(c => c.High);
        var rangeLow = range.Min(c => c.Low);
        var candleRange = last.High - last.Low;
        bool breakout;
        if (isLong)
            breakout = last.Close > rangeHigh && last.Close > last.Open &&
                       !(candleRange > 0 && (last.Close - last.Low) / candleRange < MinClosePosition);
        else
            breakout = last.Close < rangeLow && last.Close < last.Open &&
                       !(candleRange > 0 && (last.High - last.Close) / candleRange < MinClosePosition);
        if (!breakout)
        {
            result.Rejection = RejectionReason.NoBreakout;
            return result;
        }

        // Volume: se il dato manca la strategia non può confermare il breakout
        var avgVolume = candles.Skip(candles.Count - 1 - VolumePeriod).Take(VolumePeriod).Select(c => c.Volume).Average();
        if (avgVolume <= 0)
        {
            result.Rejection = RejectionReason.NoVolumeData;
            return result;
        }
        if (last.Volume < avgVolume * VolumeFactor)
        {
            result.Rejection = RejectionReason.VolumeInsufficient;
            return result;
        }

        if (isLong ? rsiNow > RsiMax : rsiNow < RsiMin)
        {
            result.Rejection = RejectionReason.RsiExtreme;
            return result;
        }

        // Stop oltre il range opposto, con buffer ATR
        var atr = CalculateAtr(candles, AtrPeriod);
        var stop = isLong ? rangeLow - atr * AtrStopBuffer : rangeHigh + atr * AtrStopBuffer;
        bool stopInvalid = isLong ? stop >= last.Close : stop <= last.Close;
        if (stopInvalid || Math.Abs(last.Close - stop) / last.Close > MaxStopPercent)
        {
            result.Rejection = RejectionReason.StopLossError;
            return result;
        }

        result.IsSignal = true;
        result.Signal = isLong ? "BUY" : "SELL";
        result.Indicators["StopLoss"] = stop;
        result.Indicators["ATR"] = atr;
        // Più forte = RS più lontana dalla sua media, breakout più ampio e volume più alto
        var rsDist = isLong ? rsNow / rsSma - 1m : 1m - rsNow / rsSma;
        var breakDist = isLong ? (last.Close - rangeHigh) / rangeHigh : (rangeLow - last.Close) / rangeLow;
        result.Indicators["SignalStrength"] = rsDist * 100m + breakDist * 100m + last.Volume / avgVolume;
        return result;
    }

    // Linea RS = Close asset / Close benchmark, sulle sole candele con lo stesso Time
    private static List<decimal> BuildRelativeStrength(List<Candle> candles, List<Candle> benchmark)
    {
        var benchByTime = new Dictionary<DateTime, decimal>(benchmark.Count);
        foreach (var b in benchmark)
            if (b.Close > 0) benchByTime[b.Time] = b.Close;

        var rs = new List<decimal>(candles.Count);
        foreach (var c in candles)
            if (benchByTime.TryGetValue(c.Time, out var bc))
                rs.Add(c.Close / bc);
        return rs;
    }

    private static decimal CalculateAtr(List<Candle> candles, int period)
    {
        var trs = new List<decimal>();
        for (int i = candles.Count - period; i < candles.Count; i++)
        {
            var c = candles[i];
            var prevClose = candles[i - 1].Close;
            trs.Add(Math.Max(c.High - c.Low, Math.Max(Math.Abs(c.High - prevClose), Math.Abs(c.Low - prevClose))));
        }
        return trs.Average();
    }
}
