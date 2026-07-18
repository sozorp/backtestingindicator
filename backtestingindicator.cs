#region Using declarations
using System;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using NinjaTrader.NinjaScript.DrawingTools;
#endregion

namespace NinjaTrader.NinjaScript.Indicators
{
	public class BacktestingIndicator : Indicator
	{
		private Button backtestButton;
		private Grid chartGrid;

		protected override void OnStateChange()
		{
			if (State == State.SetDefaults)
			{
				Description = "Ejecuta backtesting manual usando los niveles de la herramienta de dibujo Risk/Reward y exporta el resultado a CSV.";
				Name = "BacktestingIndicator";
				Calculate = Calculate.OnBarClose;
				IsOverlay = true;
				DisplayInDataBox = false;
				PaintPriceMarkers = false;
				IsSuspendedWhileInactive = false;

				RiskRewardTag = "RR1";
				RiskAmount = 500;
				CsvFilePath = System.IO.Path.Combine(
					NinjaTrader.Core.Globals.UserDataDir, "BacktestingResults.csv");
			}
			else if (State == State.Historical)
			{
				CreateCsvIfMissing();
			}
			else if (State == State.Terminated)
			{
				RemoveButton();
			}
		}

		protected override void OnBarUpdate()
		{
		}

		public override void OnRenderTargetChanged()
		{
			base.OnRenderTargetChanged();

			if (ChartControl == null)
			{
				RemoveButton();
				return;
			}

			CreateButton();
		}

		private void CreateButton()
		{
			if (backtestButton != null || ChartControl == null)
				return;

			Grid grid = ChartControl.Parent as Grid;
			if (grid == null)
				return;

			chartGrid = grid;

			backtestButton = new Button
			{
				Content = "Backtest",
				Width = 90,
				Height = 26,
				HorizontalAlignment = HorizontalAlignment.Right,
				VerticalAlignment = VerticalAlignment.Top,
				Margin = new Thickness(0, 30, 10, 0),
				Background = Brushes.DarkOrange,
				Foreground = Brushes.White,
				FontWeight = FontWeights.Bold,
				ToolTip = "Ejecuta el backtest usando los niveles del Risk/Reward configurado"
			};

			backtestButton.Click += OnBacktestButtonClick;
			chartGrid.Children.Add(backtestButton);
		}

		private void RemoveButton()
		{
			if (backtestButton != null && chartGrid != null)
			{
				backtestButton.Click -= OnBacktestButtonClick;
				chartGrid.Children.Remove(backtestButton);
			}

			backtestButton = null;
			chartGrid = null;
		}

		private void OnBacktestButtonClick(object sender, RoutedEventArgs e)
		{
			try
			{
				RunBacktest();
			}
			catch (Exception ex)
			{
				Log("BacktestingIndicator error: " + ex.Message, NinjaTrader.Cbi.LogLevel.Error);
				System.Windows.MessageBox.Show(
					"Error ejecutando el backtest: " + ex.Message,
					"BacktestingIndicator", MessageBoxButton.OK, MessageBoxImage.Error);
			}
		}

		private void RunBacktest()
		{
			RiskReward rr = FindRiskReward(RiskRewardTag);
			if (rr == null)
			{
				System.Windows.MessageBox.Show(
					string.Format("No se encontró ningún objeto Risk/Reward con el tag '{0}' en el gráfico.", RiskRewardTag),
					"BacktestingIndicator", MessageBoxButton.OK, MessageBoxImage.Warning);
				return;
			}

			double entryPrice = rr.EntryAnchor.Price;
			double stopPrice = rr.RiskAnchor.Price;
			double targetPrice = rr.RewardAnchor.Price;
			DateTime entryTime = rr.EntryAnchor.Time;

			bool isLong = targetPrice > entryPrice;

			double stopDistancePoints = Math.Abs(entryPrice - stopPrice);
			if (stopDistancePoints <= 0)
			{
				System.Windows.MessageBox.Show(
					"La distancia de Stop Loss del Risk/Reward es cero. Ajusta el nivel de stop antes de correr el backtest.",
					"BacktestingIndicator", MessageBoxButton.OK, MessageBoxImage.Warning);
				return;
			}

			double pointValue = Instrument.MasterInstrument.PointValue;
			double riskPerContract = stopDistancePoints * pointValue;
			int contracts = (int)Math.Floor(RiskAmount / riskPerContract);
			if (contracts < 1)
				contracts = 1;

			int entryBarIndex = FindBarIndexForTime(entryTime);
			if (entryBarIndex < 0)
			{
				System.Windows.MessageBox.Show(
					"No se pudo ubicar la barra correspondiente al punto de entrada del Risk/Reward en el historial cargado.",
					"BacktestingIndicator", MessageBoxButton.OK, MessageBoxImage.Warning);
				return;
			}

			string outcome;
			double exitPrice;
			DateTime exitTime;
			bool resolved = false;

			outcome = "Sin resolver";
			exitPrice = Close[0];
			exitTime = Time[0];

			for (int barIndex = entryBarIndex; barIndex <= CurrentBar; barIndex++)
			{
				int barsAgo = CurrentBar - barIndex;

				double high = High[barsAgo];
				double low = Low[barsAgo];

				bool hitStop = isLong ? low <= stopPrice : high >= stopPrice;
				bool hitTarget = isLong ? high >= targetPrice : low <= targetPrice;

				if (hitStop)
				{
					outcome = "Stop Loss";
					exitPrice = stopPrice;
					exitTime = Time[barsAgo];
					resolved = true;
					break;
				}
				if (hitTarget)
				{
					outcome = "Profit";
					exitPrice = targetPrice;
					exitTime = Time[barsAgo];
					resolved = true;
					break;
				}
			}

			if (!resolved)
				outcome = "Sin resolver (no tocó TP ni SL en el historial disponible)";

			double pnlPerContract = 0;
			if (outcome == "Profit")
				pnlPerContract = Math.Abs(targetPrice - entryPrice) * pointValue;
			else if (outcome == "Stop Loss")
				pnlPerContract = -Math.Abs(entryPrice - stopPrice) * pointValue;

			double totalPnl = pnlPerContract * contracts;

			AppendResultToCsv(
				RiskRewardTag, Instrument.MasterInstrument.Name, isLong ? "Long" : "Short",
				entryTime, entryPrice, stopPrice, targetPrice,
				RiskAmount, contracts, outcome, exitTime, exitPrice, totalPnl);

			System.Windows.MessageBox.Show(
				string.Format("Resultado: {0}\nContratos: {1}\nEntrada: {2}\nSalida: {3}\nP&L: {4:C}",
					outcome, contracts, entryPrice, exitPrice, totalPnl),
				"BacktestingIndicator", MessageBoxButton.OK, MessageBoxImage.Information);
		}

		private RiskReward FindRiskReward(string tag)
		{
			foreach (var drawObject in DrawObjects)
			{
				RiskReward rr = drawObject as RiskReward;
				if (rr != null && string.Equals(rr.Tag, tag, StringComparison.OrdinalIgnoreCase))
					return rr;
			}
			return null;
		}

		private int FindBarIndexForTime(DateTime time)
		{
			for (int barIndex = 0; barIndex <= CurrentBar; barIndex++)
			{
				int barsAgo = CurrentBar - barIndex;
				if (Time[barsAgo] >= time)
					return barIndex;
			}
			return -1;
		}

		private void CreateCsvIfMissing()
		{
			try
			{
				string directory = System.IO.Path.GetDirectoryName(CsvFilePath);
				if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
					Directory.CreateDirectory(directory);

				if (!File.Exists(CsvFilePath))
				{
					string header = "FechaBacktest,TagRiskReward,Instrumento,Direccion,FechaEntrada,PrecioEntrada,PrecioStopLoss,PrecioTakeProfit,MontoRiesgo,Contratos,Resultado,FechaSalida,PrecioSalida,PnL";
					File.WriteAllText(CsvFilePath, header + Environment.NewLine, Encoding.UTF8);
				}
			}
			catch (Exception ex)
			{
				Log("BacktestingIndicator: no se pudo crear el CSV. " + ex.Message, NinjaTrader.Cbi.LogLevel.Error);
			}
		}

		private void AppendResultToCsv(
			string tag, string instrumentName, string direction,
			DateTime entryTime, double entryPrice, double stopPrice, double targetPrice,
			double riskAmount, int contracts, string outcome,
			DateTime exitTime, double exitPrice, double pnl)
		{
			CreateCsvIfMissing();

			string line = string.Join(",", new[]
			{
				DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
				EscapeCsv(tag),
				EscapeCsv(instrumentName),
				direction,
				entryTime.ToString("yyyy-MM-dd HH:mm:ss"),
				entryPrice.ToString("0.####"),
				stopPrice.ToString("0.####"),
				targetPrice.ToString("0.####"),
				riskAmount.ToString("0.##"),
				contracts.ToString(),
				EscapeCsv(outcome),
				exitTime.ToString("yyyy-MM-dd HH:mm:ss"),
				exitPrice.ToString("0.####"),
				pnl.ToString("0.##")
			});

			File.AppendAllText(CsvFilePath, line + Environment.NewLine, Encoding.UTF8);
		}

		private string EscapeCsv(string value)
		{
			if (string.IsNullOrEmpty(value))
				return string.Empty;
			if (value.Contains(",") || value.Contains("\""))
				return "\"" + value.Replace("\"", "\"\"") + "\"";
			return value;
		}

		#region Properties

		[NinjaScriptProperty]
		[Display(Name = "Tag del Risk/Reward", Description = "Tag asignado al objeto Risk/Reward dibujado en el gráfico que se usará para el backtest.", Order = 1, GroupName = "Backtest")]
		public string RiskRewardTag { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Monto de riesgo ($)", Description = "Monto en dinero que se está dispuesto a arriesgar; se usa para calcular la cantidad de contratos.", Order = 2, GroupName = "Backtest")]
		public double RiskAmount { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Ruta del archivo CSV", Description = "Ruta completa del archivo CSV donde se acumulan los resultados de cada backtest.", Order = 3, GroupName = "Backtest")]
		public string CsvFilePath { get; set; }

		#endregion
	}
}

#region NinjaScript generated code. Neither change nor remove.

namespace NinjaTrader.NinjaScript.Indicators
{
	public partial class Indicator : NinjaTrader.Gui.NinjaScript.IndicatorRenderBase
	{
		private BacktestingIndicator[] cacheBacktestingIndicator;
		public BacktestingIndicator BacktestingIndicator(string riskRewardTag, double riskAmount, string csvFilePath)
		{
			return BacktestingIndicator(Input, riskRewardTag, riskAmount, csvFilePath);
		}

		public BacktestingIndicator BacktestingIndicator(ISeries<double> input, string riskRewardTag, double riskAmount, string csvFilePath)
		{
			if (cacheBacktestingIndicator != null)
				for (int idx = 0; idx < cacheBacktestingIndicator.Length; idx++)
					if (cacheBacktestingIndicator[idx] != null && cacheBacktestingIndicator[idx].RiskRewardTag == riskRewardTag && cacheBacktestingIndicator[idx].RiskAmount == riskAmount && cacheBacktestingIndicator[idx].CsvFilePath == csvFilePath && cacheBacktestingIndicator[idx].EqualsInput(input))
						return cacheBacktestingIndicator[idx];
			return CacheIndicator<BacktestingIndicator>(new BacktestingIndicator(){ RiskRewardTag = riskRewardTag, RiskAmount = riskAmount, CsvFilePath = csvFilePath }, input, ref cacheBacktestingIndicator);
		}
	}
}

namespace NinjaTrader.NinjaScript.MarketAnalyzerColumns
{
	public partial class MarketAnalyzerColumn : MarketAnalyzerColumnBase
	{
		public Indicators.BacktestingIndicator BacktestingIndicator(string riskRewardTag, double riskAmount, string csvFilePath)
		{
			return indicator.BacktestingIndicator(Input, riskRewardTag, riskAmount, csvFilePath);
		}

		public Indicators.BacktestingIndicator BacktestingIndicator(ISeries<double> input , string riskRewardTag, double riskAmount, string csvFilePath)
		{
			return indicator.BacktestingIndicator(input, riskRewardTag, riskAmount, csvFilePath);
		}
	}
}

namespace NinjaTrader.NinjaScript.Strategies
{
	public partial class Strategy : NinjaTrader.Gui.NinjaScript.StrategyRenderBase
	{
		public Indicators.BacktestingIndicator BacktestingIndicator(string riskRewardTag, double riskAmount, string csvFilePath)
		{
			return indicator.BacktestingIndicator(Input, riskRewardTag, riskAmount, csvFilePath);
		}

		public Indicators.BacktestingIndicator BacktestingIndicator(ISeries<double> input , string riskRewardTag, double riskAmount, string csvFilePath)
		{
			return indicator.BacktestingIndicator(input, riskRewardTag, riskAmount, csvFilePath);
		}
	}
}

#endregion

#region NinjaScript generated code. Neither change nor remove.

namespace NinjaTrader.NinjaScript.Indicators
{
	public partial class Indicator : NinjaTrader.Gui.NinjaScript.IndicatorRenderBase
	{
		private BacktestingIndicator[] cacheBacktestingIndicator;
		public BacktestingIndicator BacktestingIndicator(string riskRewardTag, double riskAmount, string csvFilePath)
		{
			return BacktestingIndicator(Input, riskRewardTag, riskAmount, csvFilePath);
		}

		public BacktestingIndicator BacktestingIndicator(ISeries<double> input, string riskRewardTag, double riskAmount, string csvFilePath)
		{
			if (cacheBacktestingIndicator != null)
				for (int idx = 0; idx < cacheBacktestingIndicator.Length; idx++)
					if (cacheBacktestingIndicator[idx] != null && cacheBacktestingIndicator[idx].RiskRewardTag == riskRewardTag && cacheBacktestingIndicator[idx].RiskAmount == riskAmount && cacheBacktestingIndicator[idx].CsvFilePath == csvFilePath && cacheBacktestingIndicator[idx].EqualsInput(input))
						return cacheBacktestingIndicator[idx];
			return CacheIndicator<BacktestingIndicator>(new BacktestingIndicator(){ RiskRewardTag = riskRewardTag, RiskAmount = riskAmount, CsvFilePath = csvFilePath }, input, ref cacheBacktestingIndicator);
		}
	}
}

namespace NinjaTrader.NinjaScript.MarketAnalyzerColumns
{
	public partial class MarketAnalyzerColumn : MarketAnalyzerColumnBase
	{
		public Indicators.BacktestingIndicator BacktestingIndicator(string riskRewardTag, double riskAmount, string csvFilePath)
		{
			return indicator.BacktestingIndicator(Input, riskRewardTag, riskAmount, csvFilePath);
		}

		public Indicators.BacktestingIndicator BacktestingIndicator(ISeries<double> input , string riskRewardTag, double riskAmount, string csvFilePath)
		{
			return indicator.BacktestingIndicator(input, riskRewardTag, riskAmount, csvFilePath);
		}
	}
}

namespace NinjaTrader.NinjaScript.Strategies
{
	public partial class Strategy : NinjaTrader.Gui.NinjaScript.StrategyRenderBase
	{
		public Indicators.BacktestingIndicator BacktestingIndicator(string riskRewardTag, double riskAmount, string csvFilePath)
		{
			return indicator.BacktestingIndicator(Input, riskRewardTag, riskAmount, csvFilePath);
		}

		public Indicators.BacktestingIndicator BacktestingIndicator(ISeries<double> input , string riskRewardTag, double riskAmount, string csvFilePath)
		{
			return indicator.BacktestingIndicator(input, riskRewardTag, riskAmount, csvFilePath);
		}
	}
}

#endregion
