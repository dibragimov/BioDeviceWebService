using System;
using System.Collections.Generic;
using System.Configuration;
using System.Data;
using System.Data.SqlClient;
using System.IO;
using System.Text;

namespace WebService
{
    public partial class BioDeviceService
    {
        #region -- Utils ----------------------------------------------------------

        /// <summary>
        /// Check that the SOAP header contains terminal ID and/or name
        /// set these values to the passed parameters
        /// </summary>
        /// <returns>true if iTermID IsNullOrEmpty or not integer>0</returns>
        private bool CheckCredentials(string method, out int deviceId, out string deviceIp)
        {
            deviceId = 0;
            deviceIp = "";

            if (Credentials == null)
            {
                Log(method, deviceIp, deviceId, true, "CheckCredentials() is NULL");
                return true; //failure
            }

            if (!string.IsNullOrEmpty(Credentials.ClockIP))
            {
                deviceIp = FixIp(Credentials.ClockIP);
            }

            try
            {
                if (!string.IsNullOrEmpty(Credentials.ClockId))
                {
                    deviceId = Convert.ToInt32(Credentials.ClockId);
                }
            }
            catch (Exception ex)
            {
                Log(method, deviceIp, deviceId, true, "CheckCredentials() failure - " + ex.Message);
                deviceId = 0;
            }

            return deviceId <= 0 || string.IsNullOrEmpty(deviceIp);
        }

        public void Log(string method, string deviceIp, int deviceId, bool isError, string message)
        {
            if (GetString("IsLogActive") != "True") return;

            try
            {
                var now = DateTime.Now;
                var logFile = Path.Combine(GetString("LogPath"), string.Format("{0:yyyyMMdd}.txt", now));
                var str = new StringBuilder();
                str.AppendFormat("{0:dd/MM/yyyy HH:mm:ss.ffffff} - {1} - ", now, method);
                if (!string.IsNullOrEmpty(deviceIp))
                    str.AppendFormat("[{0}-{1}] - ", deviceIp, deviceId);
                if (isError)
                    str.Append("ERROR - ");
                str.Append(message);

                File.AppendAllLines(logFile, new[] { str.ToString() });
            }
            catch
            {
                // ignored
            }
        }

        private static string FixIp(string deviceIp)
        {
            var result = string.Empty;
            var words = deviceIp.Split('.');

            foreach (var word in words)
            {
                if (result.Length > 0) result += ".";
                result += word.PadLeft(3, '0');
            }

            return result;
        }

        public string GetString(string key)
        {
            return ConfigurationManager.AppSettings.Get(key);
        }

        public string ConnectionString
        {
            get
            {
                return ConfigurationManager.ConnectionStrings[1].ConnectionString;
            }
        }

        private static int _acfpucode = -1;
        private static List<int> _acpccode = new List<int>() { -1 };
        private static int _acpincode = -1;

        public static int ACFPUCODE
        {
            get
            {
                if (_acfpucode < 0)
                {
                    Int32.TryParse(ConfigurationManager.AppSettings.Get("ACFPUCODE"), out _acfpucode);
                }
                return _acfpucode;
            }
        }
        public static List<int> ACPCCODE
        {
            get
            {
                if (_acpccode.Contains(-1))
                {
                    string values = ConfigurationManager.AppSettings.Get("ACPCCODE");
                    if (!string.IsNullOrEmpty(values))
                    {
                        _acpccode.Remove(-1);
                        string[] vallist = values.Split(new string[] { "," }, StringSplitOptions.RemoveEmptyEntries);
                        foreach (var item in vallist)
                        {
                            _acpccode.Add(Int32.Parse(item.Trim()));
                        }
                    }
                }
                return _acpccode;
            }
        }
        public static int ACPINCODE
        {
            get
            {
                if (_acpincode < 0)
                {
                    Int32.TryParse(ConfigurationManager.AppSettings.Get("ACPINCODE"), out _acpincode);
                }
                return _acpincode;
            }
        }

        private static int _templateType = -1;
        public static int TemplateType
        {
            get
            {
                if (_templateType < 0)
                {
                    Int32.TryParse(ConfigurationManager.AppSettings.Get("TemplateType"), out _templateType);
                }
                return _templateType;
            }
        }

        private string ReadSYTemplateIntoBase64(int deviceId, string deviceIp, string template)
        {
            int iDstLen = 0;
            byte[] dst;

            byte[] src = Encoding.UTF8.GetBytes(template);
            int iSrcLen = src.Length;
            //byte[] src = new byte[iSrcLen];
            int iRead = /*fs.Read(src, 0, */iSrcLen;//);
            //fs.Close();
            if (((iRead / 2) * 2 != iRead))
            {
                Log(GetFPUAddUsersMethod, deviceIp, deviceId, true, "Invalid template size " + iRead + " for template " + template);
                return template;
            }

            int iSrc, iDst, iLen;
            const byte FirstHighNibble = 0x60;
            const byte SecondHighNibble = 0x30;

            iSrc = iDst = 0;
            iDstLen = iRead / 2;
            iLen = iDstLen;
            dst = new byte[iLen];
            while (iLen-- > 0)
            {
                if ((src[iSrc] & 0xF0) != FirstHighNibble)
                {
                    Log(GetFPUAddUsersMethod, deviceIp, deviceId, true, "Invalid template FirstHighNibble " + (src[iSrc] & 0xF0) + " for template " + template);
                    return template;
                }
                dst[iDst] = (byte)((src[iSrc] & 0x0F) << 4);
                iSrc++;
                if ((src[iSrc] & 0xF0) != SecondHighNibble)
                {
                    Log(GetFPUAddUsersMethod, deviceIp, deviceId, true, "Invalid template SecondHighNibble " + (src[iSrc] & 0xF0) + " for template " + template);
                    return template;
                }
                dst[iDst] += (byte)(src[iSrc] & 0x0F);
                iDst++;
                iSrc++;
            }
            template = Convert.ToBase64String(dst, 0, iDstLen);

            return template;
        }

        /// <remarks>
        /// return -1 if terminal was not found in sy_Terminals
        /// </remarks>
        public int GetTerminalId(string method, string deviceIp, int deviceId)
        {
            SqlConnection Connection = null;
            SqlDataReader Reader = null;
            SqlCommand Command = null;
            string qry = String.Empty;
            int ret = -1;

            try
            {
                qry = "SELECT ID FROM sy_terminals WHERE TerminalID = @terminalId";

                //Log("GetIDFrom_sy_terminals qry = " + qry);

                Connection = new SqlConnection(ConnectionString);
                Command = new SqlCommand(qry, Connection);
                Command.Parameters.AddWithValue("@terminalId", deviceId);

                Connection.Open();
                Reader = Command.ExecuteReader();

                if (Reader.HasRows)
                {
                    Reader.Read();
                    ret = (int)Reader["ID"];
                }
            }
            catch (Exception ex)
            {
                Log(method, deviceIp, deviceId, true, string.Format("GetTerminalId({0}, {1}) Error occurred: {2}", deviceIp, deviceId, ex.ToString()));
            }
            finally
            {
                if (Reader != null) Reader.Dispose();
                if (Command != null) Command.Dispose();
                if (Connection != null) { Connection.Close(); Connection.Dispose(); }
            }
            return ret;
        }

        public int GetTerminalFPUType(string method, string deviceIp, int deviceId)
        {
            try
            {
                using (var conn = new SqlConnection(ConnectionString))
                {
                    var comm = conn.CreateCommand();
                    comm.CommandText = @"SELECT FPUType FROM sy_terminals WHERE TerminalID = @terminalId";
                    comm.Parameters.AddWithValue("@terminalId", deviceId);
                    conn.Open();
                    var obj = comm.ExecuteScalar();
                    if (obj != null && !string.IsNullOrEmpty(obj.ToString()))
                    {
                        var id = Int32.Parse(obj.ToString());
                        if (id > 0)
                        {
                            return id;
                        }
                    }
                    conn.Close();
                }
            }
            catch (Exception ex)
            {
                Log(method, deviceIp, deviceId, true, string.Format("GetTerminalFPUType({0}, {1}) Error occurred: {2}", deviceIp, deviceId, ex.ToString()));
            }

            return -1;
        }

        /// <remarks>
        /// return -1 if template_id was not found in sy_templates
        /// </remarks>
        public int GetIDFrom_sy_templates(string method, string deviceIp, int deviceId, string templateId)
        {
            SqlConnection Connection = null;
            SqlDataReader Reader = null;
            SqlCommand Command = null;
            string qry = String.Empty;
            int ret = -1;

            try
            {

                qry = String.Format("SELECT ID FROM sy_templates WHERE (TemplateId = '{0}')", templateId.Trim().PadLeft(10, '0'));

                Connection = new SqlConnection(ConnectionString);
                Command = new SqlCommand(qry, Connection);
                Connection.Open();
                Reader = Command.ExecuteReader();

                if (Reader.HasRows)
                {
                    Reader.Read();
                    ret = (int)Reader["ID"];
                }
            }
            catch (Exception ex)
            {
                Log(method, deviceIp, deviceId, true, "GetIDFrom_sy_templates() - " + ex.ToString());
            }
            finally
            {
                if (Reader != null) Reader.Dispose();
                if (Command != null) Command.Dispose();
                if (Connection != null) { Connection.Close(); Connection.Dispose(); }
            }
            return ret;
        }

        private TerminalInfo GetTerminalInfo(string method, string deviceIp, int deviceId)
        {
            var terminalInfo = new TerminalInfo();

            try
            {
                using (SqlConnection conn = new SqlConnection(ConnectionString))
                {
                    string query =
                        "SELECT ID, TimeZoneID, TerminalUtilization " +
                        "FROM [dbo].[sy_terminals] " +
                        "WHERE sy_terminals.TerminalID = @terminalId AND IsActive = 1";
                    
                    conn.Open();
                    SqlCommand cmd = new SqlCommand(query, conn);
                    cmd.Parameters.AddWithValue("@terminalId", deviceId);

                    var reader = cmd.ExecuteReader();

                    if (reader.HasRows && reader.Read())
                    {
                        terminalInfo.Id = Convert.ToInt32(reader["ID"]);
                        terminalInfo.TimezoneId = reader["TimeZoneID"].ToString();

                        int terminalUtilization = Convert.IsDBNull(reader["TerminalUtilization"])
                            ? default(int)
                            : Convert.ToInt32(reader["TerminalUtilization"]);

                        switch (terminalUtilization)
                        {
                            case 1:
                                terminalInfo.TerminalUtilization = TerminalUtilization.Registration;
                                break;
                            case 2:
                                terminalInfo.TerminalUtilization = TerminalUtilization.AccessControl;
                                break;
                            case 3:
                                terminalInfo.TerminalUtilization = TerminalUtilization.RegistrationAndAccessControl;
                                break;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Log(method, deviceIp, deviceId, true, string.Format("GetTerminalInfo({0}, {1}) Error occurred: {2}", deviceIp, deviceId, ex.ToString()));
            }

            return terminalInfo;
        }

        private DateTime GetTerminalTime(string method, string deviceIp, int deviceId, string terminalTimeZoneId)
        {
            var currentDt = DateTime.Now;

            try
            {
                if (string.IsNullOrEmpty(terminalTimeZoneId))
                    terminalTimeZoneId = TimeZoneInfo.Local.Id;

                var localTimeZone = TimeZoneInfo.Local.Id;
                var terminalTime = localTimeZone != terminalTimeZoneId
                    ? TimeZoneInfo.ConvertTimeBySystemTimeZoneId(currentDt, localTimeZone, terminalTimeZoneId)
                    : currentDt;

                Log(method, deviceIp, deviceId, false, "GetTerminalTime() Terminal time: " + terminalTime);

                return terminalTime;
            }
            catch (TimeZoneNotFoundException ex)
            {
                Log(method, deviceIp, deviceId, false, "GetTerminalTime() Error: " + ex.ToString());
            }

            return currentDt;
        }

        #endregion
    }
}