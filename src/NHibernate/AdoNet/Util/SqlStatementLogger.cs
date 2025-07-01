using System;
using System.Data;
using System.Data.Common;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

// ReSharper disable PossibleNullReferenceException
// ReSharper disable AssignNullToNotNullAttribute

namespace NHibernate.AdoNet.Util
{
	/// <summary> Centralize logging handling for SQL statements. </summary>
	public class SqlStatementLogger
	{
		private static INHibernateLogger Logger => NHibernateLogger.For("NHibernate.SQL");

		/// <summary> Constructs a new SqlStatementLogger instance.</summary>
		public SqlStatementLogger() : this(false, false)
		{
		}

		/// <summary> Constructs a new SqlStatementLogger instance. </summary>
		/// <param name="logToStdout">Should we log to STDOUT in addition to our internal logger. </param>
		/// <param name="formatSql">Should we format SQL ('prettify') prior to logging. </param>
		public SqlStatementLogger(bool logToStdout, bool formatSql)
		{
			LogToStdout = logToStdout;
			FormatSql = formatSql;
		}

		public bool LogToStdout { get; set; }

		public bool FormatSql { get; set; }

		public bool IsDebugEnabled
		{
			get { return Logger.IsDebugEnabled(); }
		}
		
		private static readonly Regex typeMatch1 = new(@"repository$|command$|handler$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
		
		private static readonly Regex parametersRegex = new Regex(@"(?<ref>@p\d+)\s*\=\s*(?<value>.+?) \s* \[Type\:\s*(?<type>[^\(]+?) \s \((?<size>[^\)\]]+)\)\]", RegexOptions.IgnorePatternWhitespace | RegexOptions.Compiled);

		private static SqlParameterInfo[] ParseParameters(string message)
		{
			//e.g.
			//select SCOPE_IDENTITY();@p0 = 'WorkZone' [Type: String (4000)], @p1 = 06/04/2021 14:08:14 [Type: DateTime (0)], @p2 = NULL [Type: String (4000)], @p3 = NULL [Type: String (4000)], @p4 = False [Type: Boolean (0)], @p5 = False [Type: Boolean (0)], @p6 = NULL [Type: String (4000)], @p7 = True [Type: Boolean (0)], @p8 = False [Type: Boolean (0)], @p9 = False [Type: Boolean (0)], @p10 = NULL [Type: Int32 (0)], @p11 = NULL [Type: Int32 (0)], @p12 = 1 [Type: Int32 (0)], @p13 = NULL [Type: Int32 (0)], @p14 = False [Type: Boolean (0)], @p15 = NULL [Type: Int32 (0)], @p16 = NULL [Type: Int32 (0)], @p17 = 216697 [Type: Int32 (0)] 

			var matches = parametersRegex.Matches(message);
			return matches.Cast<Match>().Select(m => new SqlParameterInfo
			{
				Ref = m.Groups["ref"].Value,
				Size = m.Groups["size"].Value,
				Type = m.Groups["type"].Value,
				Value = m.Groups["value"].Value
			}).ToArray();
		}
		
		private static string ReplaceParameters(string msg)
		{
			var parameters = ParseParameters(msg);
			var firstParamMatch = parametersRegex.Match(msg);
			var sqlWithoutParameters = firstParamMatch.Success
				? msg.Substring(0, firstParamMatch.Index)
				: msg; // everything after the first param isn't SQL
			var sqlWithParametersSubstituted = parameters.Aggregate(sqlWithoutParameters, (s, p) => Regex.Replace(s, @$"{Regex.Escape(p.Ref)}\b", TranslateValue(p))); // doesn't need to match word boundary at start, as starts with @
			var sqlMultiLine = Regex.Replace(sqlWithParametersSubstituted, @"\b(?:SELECT|FROM|WHERE|INNER|JOIN|GROUP)\b", "\r\n$0");
			return sqlMultiLine;
		}
		
		private static string TranslateValue(SqlParameterInfo parameterInfo)
		{
			switch(parameterInfo.Type)
			{
				case "Boolean": return "True".Equals(parameterInfo.Value, System.StringComparison.OrdinalIgnoreCase) ? "1" : "0";
				case "DateTime": case "DateTime2": return WriteDateValue(parameterInfo.Value);
				default: return parameterInfo.Value; //strings already have single quotes round them
			}
		}

		private static string WriteDateValue(string dateParamValue)
		{
			if (DateTime.TryParse(dateParamValue, out var date)) return $"'{date:yyyy-MM-dd HH:mm:ss.fff}'";
			return string.Equals("null", dateParamValue, StringComparison.InvariantCultureIgnoreCase) ?
				dateParamValue : $"cast('{dateParamValue}' as datetime)";
		}

		private static readonly Regex stackFrameOfInterest = new(@"^\s+at Payroll", RegexOptions.Compiled);
		private static string CommentedCallStack(StackTrace stackTrace)
		{
			var stringBuilder = new StringBuilder();
			using (var stringWriter = new StringWriter(stringBuilder))
			{
				stringWriter.WriteLine();
				stringWriter.WriteLine("/*");
				bool uninterestingFramesSkipped = false; // only write one set of dots per load of uninteresting frames
				foreach (var stackTraceLine in stackTrace.ToString().Split(new[] {'\n'}, StringSplitOptions.RemoveEmptyEntries).Select(s => s.Trim('\r')))
				{
					if (stackFrameOfInterest.IsMatch(stackTraceLine))
					{
						if (uninterestingFramesSkipped)
						{
							stringWriter.WriteLine("...");
						}

						stringWriter.WriteLine(stackTraceLine);
						uninterestingFramesSkipped = false;
					}
					else
					{
						uninterestingFramesSkipped = true;
					}
				}
				stringWriter.WriteLine("*/");
			}

			return stringBuilder.ToString();
		}

		private static void LogSql(string message, bool writeDebugOutputToFile)
		{
			var stackTrace = new StackTrace(true);
			// ideally, a command, handler, or repository
			var topFrameOfInterest = stackTrace.GetFrames().FirstOrDefault(
				sf =>
				{
					var declaringType = sf.GetMethod()?.DeclaringType;
					return declaringType != null && declaringType.Name != null && declaringType.Namespace != null &&
					       declaringType.Namespace.StartsWith("Payroll") && typeMatch1.IsMatch(declaringType.Name);
				})
			               ?? // but if not, anything in Payroll
			    stackTrace.GetFrames().FirstOrDefault(sf => sf.GetMethod()?.DeclaringType?.Namespace?.StartsWith("Payroll") ?? false);

			if (topFrameOfInterest != null) // don't log if it's not even in Payroll
			{
				var loggerName = "BT.Debug.NHibernate.SQL";
				var formattedMessage = ReplaceParameters(message) + CommentedCallStack(stackTrace);
				NLog.LogManager.GetLogger(loggerName).Info($"{topFrameOfInterest.GetMethod().DeclaringType.FullName}\r\n{formattedMessage}");

				if (writeDebugOutputToFile)
				{
					try
					{
						var fileName = $"{topFrameOfInterest.GetMethod().DeclaringType.Name}.{topFrameOfInterest.GetMethod().Name}.log";
						File.AppendAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, fileName), formattedMessage);
						File.AppendAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, fileName), "/* ========================================================= */");
					}
					catch (Exception e)
					{
						try
						{
							File.AppendAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "NHibernate.log"), e.ToString());
						}
						catch (Exception)
						{
							// ignored
						}
					}
				}
			}
		}

		private const string logSqlToFileMarker = ".nhlogsqltofile";
		/// <summary> Log a DbCommand. </summary>
		/// <param name="message">Title</param>
		/// <param name="command">The SQL statement. </param>
		/// <param name="style">The requested formatting style. </param>
		public virtual void LogCommand(string message, DbCommand command, FormatStyle style)
		{
			var writeDebugOutputToFile = File.Exists(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, logSqlToFileMarker));
			
			if ((!Logger.IsDebugEnabled() && !LogToStdout || string.IsNullOrEmpty(command.CommandText)) && !writeDebugOutputToFile)
			{
				return;
			}

			style = DetermineActualStyle(style);
			string statement = style.Formatter.Format(GetCommandLineWithParameters(command));
			string logMessage;
			if (string.IsNullOrEmpty(message))
			{
				logMessage = statement;
			}
			else
			{
				logMessage = message + statement;
			}
			LogSql(logMessage, writeDebugOutputToFile);
			Logger.Debug(logMessage);
			if (LogToStdout)
			{
				Console.Out.WriteLine("NHibernate: " + statement);
			}
		}

		/// <summary> Log a DbCommand. </summary>
		/// <param name="command">The SQL statement. </param>
		/// <param name="style">The requested formatting style. </param>
		public virtual void LogCommand(DbCommand command, FormatStyle style)
		{
			LogCommand(null, command, style);
		}

		public string GetCommandLineWithParameters(DbCommand command)
		{
			string outputText;

			if (command.Parameters.Count == 0)
			{
				outputText = command.CommandText;
			}
			else
			{
				var output = new StringBuilder(command.CommandText.Length + (command.Parameters.Count*20));
				output.Append(command.CommandText.TrimEnd(' ', ';', '\n'));
				output.Append(";");

				int count = command.Parameters.Count;
				bool appendComma = false;
				for (int i = 0; i < count; i++)
				{
					if (appendComma)
					{
						output.Append(", ");
					}
					appendComma = true;
					var p = command.Parameters[i];
					output.AppendFormat(
						"{0} = {1} [Type: {2}]", p.ParameterName, GetParameterLoggableValue(p), GetParameterLoggableType(p));
				}
				outputText = output.ToString();
			}
			return outputText;
		}

		private static string GetParameterLoggableType(DbParameter dataParameter)
		{
			return dataParameter.DbType + " (" + dataParameter.Size + ":" + dataParameter.Scale + ":" + dataParameter.Precision + ")";
		}

		public string GetParameterLoggableValue(DbParameter parameter)
		{
			const int maxLoggableStringLength = 1000;

			if (parameter.Value == null || DBNull.Value.Equals(parameter.Value))
			{
				return "NULL";
			}

			if (IsStringType(parameter.DbType))
			{
				return $"'{TruncateWithEllipsis(parameter.Value.ToString(), maxLoggableStringLength).Replace("'", "''")}'";
			}

			if (parameter.Value is DateTime)
				return ((DateTime) parameter.Value).ToString("O");

			if (parameter.Value is DateTimeOffset)
				return ((DateTimeOffset) parameter.Value).ToString("O");

			var buffer = parameter.Value as byte[];
			if (buffer != null)
			{
				return GetBufferAsHexString(buffer);
			}

			return parameter.Value.ToString();
		}

		private static string GetBufferAsHexString(byte[] buffer)
		{
			const int maxBytes = 128;
			int bufferLength = buffer.Length;

			var sb = new StringBuilder(maxBytes*2 + 8);
			sb.Append("0x");
			for (int i = 0; i < bufferLength && i < maxBytes; i++)
			{
				sb.Append(buffer[i].ToString("X2"));
			}
			if (bufferLength > maxBytes)
			{
				sb.Append("...");
			}
			return sb.ToString();
		}

		private static bool IsStringType(DbType dbType)
		{
			return DbType.String.Equals(dbType) || DbType.AnsiString.Equals(dbType)
			       || DbType.AnsiStringFixedLength.Equals(dbType) || DbType.StringFixedLength.Equals(dbType);
		}

		public FormatStyle DetermineActualStyle(FormatStyle style)
		{
			return FormatSql ? style : FormatStyle.None;
		}

		public void LogBatchCommand(string batchCommand)
		{
			Logger.Debug(batchCommand);
			if (LogToStdout)
			{
				Console.Out.WriteLine("NHibernate: " + batchCommand);
			}
		}

		private string TruncateWithEllipsis(string source, int length)
		{
			const string ellipsis = "...";
			if (source.Length > length)
			{
				return source.Substring(0, length) + ellipsis;
			}
			return source;
		}
	}
}
