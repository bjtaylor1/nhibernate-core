using System;
using System.Data.Common;
using System.Text.RegularExpressions;

namespace NHibernate.Connection
{
	/// <summary>
	/// A ConnectionProvider that uses an IDriver to create connections.
	/// </summary>
	public partial class DriverConnectionProvider : ConnectionProvider
	{
		private static readonly INHibernateLogger log = NHibernateLogger.For(typeof(DriverConnectionProvider));

		/// <summary>
		/// Closes and Disposes of the <see cref="DbConnection"/>.
		/// </summary>
		/// <param name="conn">The <see cref="DbConnection"/> to clean up.</param>
		public override void CloseConnection(DbConnection conn)
		{
			base.CloseConnection(conn);
			conn.Dispose();
		}

		/// <summary>
		/// Gets a new open <see cref="DbConnection"/> through 
		/// the <see cref="NHibernate.Driver.IDriver"/>.
		/// </summary>
		/// <returns>
		/// An Open <see cref="DbConnection"/>.
		/// </returns>
		/// <exception cref="Exception">
		/// If there is any problem creating or opening the <see cref="DbConnection"/>.
		/// </exception>
		public override DbConnection GetConnection(string connectionString)
		{
			log.Debug("Obtaining DbConnection from Driver");
			var conn = Driver.CreateConnection();
			try
			{
				conn.ConnectionString = connectionString;
				conn.Open();
			}
			catch (Exception e)
			{
				conn.Dispose();
				throw new Exception($"NHibernate encountered {e.GetType().Name} connecting to {AnonymizeConnectionString(connectionString)}");
			}
			
			return conn;
		}

		/// <summary>
		/// Anonymizes sensitive information (like passwords) in a connection string for logging purposes.
		/// </summary>
		/// <param name="connectionString">The connection string to anonymize.</param>
		/// <returns>The connection string with sensitive values replaced with ***.</returns>
		private static string AnonymizeConnectionString(string connectionString)
		{
			if (string.IsNullOrEmpty(connectionString))
				return connectionString;

			// Pattern to match common password parameter names (case-insensitive)
			// Matches: password=value, pwd=value, pass=value, etc.
			var passwordPattern = @"(password|pwd|pass)\s*=\s*[^;]*";
			
			return Regex.Replace(connectionString, passwordPattern, "$1=***", RegexOptions.IgnoreCase);
		}
	}
}
