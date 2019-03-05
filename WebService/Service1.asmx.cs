using System;
using System.Data;
using System.Collections.Generic;
using System.IO;
using System.Web.Services;
using System.Web.Services.Protocols;
using System.Data.SqlClient;
using System.Linq;


namespace WebService
{
    /// <summary>
    /// Summary description for BioDeviceService
    /// </summary>
    [WebService(Namespace = "PrivateCompany/Services")]
    [WebServiceBinding(ConformsTo = WsiProfiles.BasicProfile1_1)]
    [System.ComponentModel.ToolboxItem(false)]
    public partial class BioDeviceService : System.Web.Services.WebService
    {
        private const string GetFPUAddUsersMethod = "GetFPUAddUsers";
        private const string TestMethod = "Test";
        private const string HeartbeatMethod = "Heartbeat";
        private const string FPUAddUsersAckMethod = "FPUAddUsersAck";
        private const string GetFPUDeleteUsersMethod = "GetFPUDeleteUsers";
        private const string FPUDeleteUsersAckMethod = "FPUDeleteUsersAck";
        private const string CRValidEmployeeMethod = "CRValidEmployee";
        private const string CRSendTransactionMethod = "CRSendTransaction";
        private const string GetAppFilesAndTablesMethod = "GetAppFilesAndTables";
        private const string CRGetEmployeeTableMethod = "CRGetEmployeeTable";
        private const string SendFPUTemplateMethod = "SendFPUTemplate";

        #region -- General ----------------------------------------------------------

        public ClockCredentials Credentials;

        private enum RetStatus
        {
            NotAllowed = -4,    // Cardholder is not allowed to use this function (usually OUT)
            CantSave = -3,      // Can not save transaction
            Error = -2,         // General error in the web service
            InvalidInp = -1,    // Cardholder is not allowed to use this input source (not used at the moment)
            NotValid = 0,       // Cardholder not found or not valid
            Ok = 1              // Ok
        }

        #endregion

        #region -- test

        public struct TesterRes
        {
            public string DecryptedTemplate;
        }

        [WebMethod(Description = "test program")]
        [SoapDocumentMethod]
        [SoapHeader("Credentials", Direction = SoapHeaderDirection.In)]
        public TesterRes Test(int id)
        {
            SqlConnection Connection = null;
            SqlDataReader Reader = null;
            SqlCommand Command = null;
            string qry = String.Format("SELECT Template FROM sy_templates WHERE id={0}", id);
            string ret = String.Empty;

            TesterRes res = new TesterRes();

            try
            {
                Connection = new SqlConnection(ConnectionString);
                Command = new SqlCommand(qry, Connection);
                Connection.Open();
                Reader = Command.ExecuteReader();

                Reader.Read();
                res.DecryptedTemplate = Decrypt(Reader["Template"].ToString());
            }
            catch (Exception ex)
            {
                Log(TestMethod, string.Empty, 0, true, ex.ToString());
            }
            finally
            {
                if (Reader != null) Reader.Dispose();
                if (Command != null) Command.Dispose();
                if (Connection != null) { Connection.Close(); Connection.Dispose(); }
            }
            return res;
        }

        #endregion

        #region -- Heartbeat ----------------------------------------------------------

        private int GetHeartbeatStatus(int deviceId, string deviceIp)
        {
            // STATUSES
            //  need sync => 5
            //  has new templates, need full resync => 6
            //  has new templates, resync new only => 8
            //  general OK => 1

            int status = 1;

            // set last time online
            try
            {
                using (SqlConnection conn = new SqlConnection(ConnectionString))
                {
                    conn.Open();
                    SqlCommand comm = new SqlCommand("UPDATE [dbo].[sy_terminals] SET LastTimeOnline=@Date WHERE [TerminalID] = @IdOfTerminal AND [TerminalType]=1 ", conn);
                    comm.Parameters.Add("@IdOfTerminal", SqlDbType.Int).Value = deviceId;
                    comm.Parameters.Add("@Date", SqlDbType.DateTime).Value = DateTime.Now;
                    comm.ExecuteNonQuery();
                    conn.Close();
                }
            }
            catch (Exception ex)
            {
                Log(HeartbeatMethod, deviceIp, deviceId, true, ex.ToString());
            }

            string TERMINALSTATUSSELECT = @"
                IF(NOT EXISTS(SELECT GroupID FROM [dbo].[TerminalsInGroups] WHERE TerminalID=@IdOfTerminal))
	                SELECT COUNT(Template) 
                    FROM sy_templates 
                        INNER JOIN sy_templates_statuses ON sy_templates.Status = sy_templates_statuses.ID
                        INNER JOIN sy_employee ON sy_employee.EmpId=sy_templates.TemplateId
	                WHERE Template IS NOT NULL
	                    AND Template <> ''
                        AND sy_employee.[Status] IN ('A','N')  
                        AND sy_templates.ID NOT IN (SELECT TemplateID FROM sy_template_sync_terminals WHERE TerminalID = @IdOfTerminal)
                ELSE
	                SELECT COUNT(Template) 
                    FROM sy_templates 
                        INNER JOIN sy_templates_statuses ON sy_templates.Status = sy_templates_statuses.ID
	                    INNER JOIN sy_employee ON sy_employee.EmpId=sy_templates.TemplateId
	                WHERE Template IS NOT NULL
	                    AND Template <> ''
                        AND sy_employee.[Status] IN ('A','N') 
                        AND sy_templates.ID NOT IN (SELECT TemplateID FROM sy_template_sync_terminals WHERE TerminalID = @IdOfTerminal)
	                    AND TemplateId IN (SELECT EmpId FROM [dbo].[EmployeesInGroups] WHERE GroupID IN (SELECT GroupID FROM [dbo].[TerminalsInGroups] WHERE TerminalID=@IdOfTerminal))";
            try
            {
                var terminalId = GetTerminalId(HeartbeatMethod, deviceIp, deviceId);

                using (SqlConnection conn = new SqlConnection(ConnectionString))
                {
                    SqlCommand comm = new SqlCommand(TERMINALSTATUSSELECT, conn);
                    comm.Parameters.Add("@IdOfTerminal", SqlDbType.Int).Value = terminalId;

                    conn.Open();
                    object obj = comm.ExecuteScalar();

                    if (obj != null && !string.IsNullOrEmpty(obj.ToString()))
                    {
                        int i = Int32.Parse(obj.ToString());
                        if (i > 0)
                        {
                            status = 8;
                            //Log("GetHeartbeatStatus() Number of templates to send: " + i);
                        }
                        //else
                        //    Log("GetHeartbeatStatus() No templates to send.");
                    }
                    conn.Close();
                }
            }
            catch (Exception ex)
            {
                Log(HeartbeatMethod, deviceIp, deviceId, true, ex.ToString());
            }

            //// if no templates - check for files
            if (status == 1)
            {
                try
                {
                    using (SqlConnection conn = new SqlConnection(ConnectionString))
                    {
                        conn.Open();
                        SqlCommand cmd = new SqlCommand("SELECT NeedSync FROM sy_terminals WHERE TerminalID=@ID", conn);
                        cmd.Parameters.Add("@ID", SqlDbType.Int).Value = deviceId;
                        object obj = cmd.ExecuteScalar();
                        if (obj != null && !string.IsNullOrEmpty(obj.ToString()))
                        {
                            if (obj.ToString().Equals("1"))
                            {
                                status = 5;//// tables
                                Log(HeartbeatMethod, deviceIp, deviceId, false, "Need table sync");
                            }
                        }
                        conn.Close();
                    }
                }
                catch (Exception ex)
                {
                    Log(HeartbeatMethod, deviceIp, deviceId, true, ex.ToString());
                }
            }

            return status;
        }

        private string GetMsgByTerminalID(int deviceId, string deviceIp)
        {
            string msg = String.Empty;
            int nowTotalMinutes = DateTime.Now.Hour * 60 + DateTime.Now.Minute;
            int terminalId = GetTerminalId(HeartbeatMethod, deviceIp, deviceId);

            SqlConnection Connection = null;
            SqlDataReader Reader = null;
            SqlCommand Command = null;

            try
            {
                string qry = String.Format(
                    "SELECT Course_Description AS msg FROM TimeTable " +
                    "WHERE (TerminalID = {0}) AND (Date = '{1}') AND " +
                    "(StartTime <= {2}) AND (EndTime >= {3}) AND (At_Activated IS NOT NULL)",
                    terminalId,
                    DateTime.Now.ToString("yyyy-MM-dd"),
                    nowTotalMinutes,
                    nowTotalMinutes);

                //Log("GetMsgByTerminalID() qry=" + qry);

                Connection = new SqlConnection(ConnectionString);
                Command = new SqlCommand(qry, Connection);
                Connection.Open();

                Reader = Command.ExecuteReader();

                if (Reader.Read())
                {
                    msg = Reader["msg"].ToString();
                }

            }
            catch (Exception ex)
            {
                Log(HeartbeatMethod, deviceIp, deviceId, true, ex.ToString());
            }
            finally
            {
                if (Reader != null) Reader.Dispose();
                if (Command != null) Command.Dispose();
                if (Connection != null) { Connection.Close(); Connection.Dispose(); }
            }
            return msg;
        }

        public struct ClockResult
        {
            /*<CheckFPU>int</CheckFPU>
              <CheckPinCode>int</CheckPinCode>
              <PinCode>string</PinCode>
              <Employee>string</Employee>
              <Id>int</Id>
              <Status>int</Status>
              <Message>string</Message>
              <HostTime>dateTime</HostTime>
              <image>string</image>
            */
            public int CheckFPU;
            public int CheckPinCode;
            public string PinCode;
            public string Employee;
            public int Id;
            public int Status;
            public string Message;
            public DateTime HostTime;
            public string image;
        }

        [WebMethod(Description = "Heartbeat - Terminal heartbeat")]
        [SoapDocumentMethod]
        [SoapHeader("Credentials", Direction = SoapHeaderDirection.InOut)]
        public ClockResult Heartbeat(DateTime utcTime)
        {
            int deviceId;
            string deviceIp;

            ClockResult cres = new ClockResult();

            cres.Status = (int)RetStatus.NotValid;
            cres.Message = "";
            cres.HostTime = DateTime.Now;
            cres.CheckFPU = 0;
            cres.CheckPinCode = 0;
            cres.PinCode = string.Empty;
            cres.Employee = string.Empty;
            cres.image = string.Empty;

            if (CheckCredentials(HeartbeatMethod, out deviceId, out deviceIp))
            {
                cres.Message = "Error: Credentials problem";
                return cres;
            }

            var terminalInfo = GetTerminalInfo(HeartbeatMethod, deviceIp, deviceId);
            var terminalTime = GetTerminalTime(HeartbeatMethod, deviceIp, deviceId, terminalInfo.TimezoneId);

            cres.Id = deviceId;
            cres.HostTime = terminalTime;
            cres.Message = GetMsgByTerminalID(deviceId, deviceIp);
            cres.Status = GetHeartbeatStatus(deviceId, deviceIp);//(int)RetStatus.Ok;

            Log(HeartbeatMethod, deviceIp, deviceId, false, string.Format("Time: {0: HH:mm:ss}, Message: {1}, Status: {2}", cres.HostTime, cres.Message, cres.Status));

            return cres;
        }

        #endregion

        #region -- GetFPUAddUsers ----------------------------------------------------

        private void RemoveTerminalOldRecords(int deviceId, string deviceIp)
        {
            Log(GetFPUAddUsersMethod, deviceIp, deviceId, false, "RemoveTerminalOldRecords() - Started");

            SqlConnection conn = null;
            SqlCommand cmd = null;
            string qry = string.Format("delete from sy_template_sync_terminals where TerminalID={0}",
                deviceId);

            try
            {
                conn = new SqlConnection(ConnectionString);
                conn.Open();
                cmd = new SqlCommand(qry, conn);
                cmd.ExecuteNonQuery();

                Log(GetFPUAddUsersMethod, deviceIp, deviceId, false, "RemoveTerminalOldRecords() - Success");
            }
            catch (Exception ex)
            {
                Log(GetFPUAddUsersMethod, deviceIp, deviceId, true, ex.ToString());
            }
            finally
            {
                if (cmd != null) cmd.Dispose();
                if (conn != null) { conn.Close(); conn.Dispose(); }
            }
        }

        public struct UserDetails
        {
            public string ID;
            public string Name;
            public string[] Templates;
        }

        public struct GetFPUAddUsersResult
        {
            public UserDetails[] Users;
            public bool DeleteAllUsers;
            public bool UsersEnd;
        }

        private string Decrypt(string str)
        {
            if (GetString("DecryptTemplates").ToLower().Equals("true"))
            {
                EncryptorDecryptor.SimpleAES aes = new EncryptorDecryptor.SimpleAES();
                return aes.DecryptToString(str);
            }

            return str;
        }

        [WebMethod(Description = "GetFPUAddUsers - Request new fingerprint templates")]
        [SoapDocumentMethod]
        [SoapHeader("Credentials", Direction = SoapHeaderDirection.In)]
        public GetFPUAddUsersResult GetFPUAddUsers(int PortionNumber, int GetAllTemplates)
        {
            GetFPUAddUsersResult gfres = new GetFPUAddUsersResult();
            gfres.DeleteAllUsers = false;
            gfres.UsersEnd = true;

            int deviceId;
            string deviceIp;
            bool getAllTemplates = (GetAllTemplates == 1);

            if (CheckCredentials(GetFPUAddUsersMethod, out deviceId, out deviceIp))
            {
                return gfres;
            }

            Log(GetFPUAddUsersMethod, deviceIp, deviceId, false,
                string.Format("Started - PortionNumber: {0}, GetAllTemplates: {1}", PortionNumber, getAllTemplates));

            try
            {
                var terminalId = GetTerminalId(GetFPUAddUsersMethod, deviceIp, deviceId);
                var terminalFpuType = GetTerminalFPUType(GetFPUAddUsersMethod, deviceIp, deviceId);

                if (PortionNumber == 1 && getAllTemplates)
                    RemoveTerminalOldRecords(deviceId, deviceIp);

                int iMaxEmpToSend = 100;

                if (!int.TryParse(GetString("GetFPUAddUsers_iMaxEmpToSend"), out iMaxEmpToSend))
                    iMaxEmpToSend = 50;

                if (iMaxEmpToSend < 1) iMaxEmpToSend = 50;

                var qry = String.Format(@" 
                    IF(EXISTS(SELECT * FROM [dbo].[TerminalsInGroups] WHERE TerminalID = @terminalId))
                        SELECT t.ID, t.TemplateId, t.Template, t.Status, t.Terminal, e.FirstName, e.LastName, t.TemplateType, t.TemplateStoredAs
                        FROM sy_templates t
	                        INNER JOIN sy_employee e ON t.TemplateId = e.EmpId
                        WHERE e.EmpId IN
                           (SELECT TOP ({0}) EmpId
	                        FROM (
		                        SELECT DISTINCT e.EmpId
		                        FROM sy_templates t
			                        INNER JOIN sy_employee e ON t.TemplateId = e.EmpId
			                        INNER JOIN EmployeesInGroups eig ON eig.EmpId = e.EmpId
		                        WHERE e.[Status] IN ('A', 'N')
			                        AND eig.GroupID IN (SELECT GroupID FROM [dbo].[TerminalsInGroups] WHERE TerminalID = @terminalId)
			                        AND t.TemplateId NOT IN
				                        (SELECT sy_templates.TemplateId
                                         FROM sy_template_sync_terminals
											INNER JOIN sy_templates ON sy_template_sync_terminals.TemplateID = sy_templates.ID
				                         WHERE TerminalID = @terminalId)) employees)
                        ORDER BY e.EmpId
                    ELSE
                        SELECT t.ID, t.TemplateId, t.Template, t.Status, t.Terminal, e.FirstName, e.LastName, t.TemplateType, t.TemplateStoredAs
                        FROM sy_templates t
	                        INNER JOIN sy_employee e ON t.TemplateId = e.EmpId
                        WHERE e.EmpId IN
                           (SELECT TOP ({0}) EmpId
	                        FROM (
		                        SELECT DISTINCT e.EmpId
		                        FROM sy_templates t
			                        INNER JOIN sy_employee e ON t.TemplateId = e.EmpId
		                        WHERE e.[Status] IN ('A', 'N')
			                        AND t.TemplateId NOT IN
				                        (SELECT sy_templates.TemplateId
                                         FROM sy_template_sync_terminals
											INNER JOIN sy_templates ON sy_template_sync_terminals.TemplateID = sy_templates.ID
				                         WHERE TerminalID = @terminalId)) employees)
                        ORDER BY e.EmpId", iMaxEmpToSend);

                using (var connection = new SqlConnection(ConnectionString))
                using (var command = new SqlCommand(qry, connection))
                {
                    command.Parameters.AddWithValue("@terminalId", terminalId);

                    connection.Open();
                    var reader = command.ExecuteReader();

                    if (!reader.HasRows)
                    {
                        Log(GetFPUAddUsersMethod, deviceIp, deviceId, false, "No new templates");
                        return gfres;
                    }

                    var userDetails = new Dictionary<string, UserDetails>();
                    var userTemplates = new Dictionary<string, List<string>>();
                    var noOfRecordsInDb = 0;

                    while (reader.Read())
                    {
                        noOfRecordsInDb++;

                        var empId = reader["TemplateId"].ToString();
                        var employeeName = reader["FirstName"].ToString().Trim() + " " + reader["LastName"].ToString().Trim();

                        if (!userDetails.ContainsKey(empId))
                        {
                            userDetails.Add(empId, new UserDetails
                            {
                                ID = empId,
                                Name = Helpers.FrenchToAscii(employeeName)
                            });
                        }

                        if (!userTemplates.ContainsKey(empId))
                            userTemplates.Add(empId, new List<string>());

                        var templates = userTemplates[empId];
                        var template = Decrypt(reader["Template"].ToString());

                        if (!reader.IsDBNull(reader.GetOrdinal("TemplateType"))
                            && !reader.IsDBNull(reader.GetOrdinal("TemplateStoredAs"))) // if template data is specified in DB
                        {
                            var templateType = reader.GetInt32(reader.GetOrdinal("TemplateType"));
                            var templateStoredAs = reader.GetInt32(reader.GetOrdinal("TemplateStoredAs"));

                            if (templateType == 1 && templateStoredAs == 1 && terminalFpuType < 5 && terminalFpuType > 0) // SY ASCII
                            {
                                template = ReadSYTemplateIntoBase64(deviceId, deviceIp, template);
                                templates.Add(template);
                            }
                            else if ((templateType == 2 || templateType == 4) && templateStoredAs == 2 &&
                                     ((terminalFpuType < 5 && terminalFpuType > 0) || terminalFpuType == 6)) // Suprema (or biolite) Base64
                            {
                                templates.Add(template);
                            }
                            else if (templateType == 3 && templateStoredAs == 2 && terminalFpuType == 5) // BioSmack Base64
                            {
                                templates.Add(template);
                            }
                            else
                            {
                                Log(GetFPUAddUsersMethod, deviceIp, deviceId, false,
                                    string.Format(
                                        "Employee {0} ({1}): Template skipped due to incompatible types - terminal type: {2}, template type: {3}, template stored as: {4}",
                                        employeeName, empId, terminalFpuType, templateType, templateStoredAs));

                                //add to sync table to prevent infinite loops
                                try
                                {
                                    using (var connectionForSync = new SqlConnection(ConnectionString))
                                    using (var commandForSync = connectionForSync.CreateCommand())
                                    {
                                        commandForSync.CommandText = @"
                                        INSERT INTO [sy_template_sync_terminals] ([TemplateID], [TerminalID], [IsIncompatibleType], [Sync_Date]) 
                                        VALUES (@templateId, @terminalId, 1, getdate())";

                                        connectionForSync.Open();
                                        commandForSync.Parameters.AddWithValue("@templateId", empId);
                                        commandForSync.Parameters.AddWithValue("@terminalId", terminalId);
                                        commandForSync.ExecuteNonQuery();
                                    }

                                    Log(GetFPUAddUsersMethod, deviceIp, deviceId, false,
                                        string.Format("Template is marked as skipped for {0} ({1})", employeeName, empId));
                                }
                                catch (Exception ex)
                                {
                                    Log(GetFPUAddUsersMethod, deviceIp, deviceId, true,
                                        string.Format("Error marking template as skipped for {0} ({1}): {2}", employeeName, empId, ex));
                                }
                            }
                        }
                        else
                        {
                            //unknown types
                            if (TemplateType == 2) //Biosmack
                            {
                                templates.Add(template);
                            }
                            else if (TemplateType == 0) // SYtemplate
                            {
                                template = ReadSYTemplateIntoBase64(deviceId, deviceIp, template);
                                templates.Add(template);
                            }
                            else //Biostore and Suprema
                            {
                                templates.Add(template);
                            }
                        }
                    }

                    //merge data
                    var counter = 0;
                    var allEmployees = new List<UserDetails>();

                    foreach (var badge in userDetails.Keys)
                    {
                        var details = userDetails[badge];

                        if (userTemplates.ContainsKey(badge))
                        {
                            details.Templates = userTemplates[badge].ToArray();
                            if (details.Templates.Length > 0) //only if we have actual templates
                            {
                                //Synergy A Suprema should always get two templates
                                if (terminalFpuType == 4 && details.Templates.Length == 1)
                                {
                                    details.Templates = new[] { details.Templates[0], details.Templates[0] }; //send same template twice

                                    Log(GetFPUAddUsersMethod, deviceIp, deviceId, false,
                                        string.Format(
                                            "Employee #{0}: {1} ({2}). Template duplicated for Synergy A Suprema",
                                            counter, details.ID, details.Name));
                                }

                                allEmployees.Add(details);
                                counter++;

                                Log(GetFPUAddUsersMethod, deviceIp, deviceId, false,
                                    string.Format("Employee #{0}: {1} ({2}). No of tempates: {3}", counter, details.ID,
                                        details.Name, details.Templates.Length));
                            }
                        }
                    }

                    gfres.Users = allEmployees.ToArray();
                    if (noOfRecordsInDb >= iMaxEmpToSend) gfres.UsersEnd = false;

                    //if no templates were actually sent - no need to ask again
                    if (gfres.Users.Length == 0) gfres.UsersEnd = true;
                }
            }
            catch (Exception ex)
            {
                Log(GetFPUAddUsersMethod, deviceIp, deviceId, true, ex.ToString());
            }

            Log(GetFPUAddUsersMethod, deviceIp, deviceId, false,
                string.Format("Records send: {0}, more to send: {1}", gfres.Users.Length, !gfres.UsersEnd));

            return gfres;
        }

        #endregion

        #region -- FPUAddUsersAck ----------------------------------------------------

        public struct FpuUserAdd
        {
            public string ID;
            public int TemplateNumber;
            public int Result;
        }

        public class FPUAddUsersAckResult
        {
            public int Status;
        }

        [WebMethod(Description = "FPUAddUsersAck - Acknowledge sent fingerprints")]
        [SoapDocumentMethod]
        [SoapHeader("Credentials", Direction = SoapHeaderDirection.In)]
        public FPUAddUsersAckResult FPUAddUsersAck(FpuUserAdd[] UsersToAdd)
        {
            FPUAddUsersAckResult uar = new FPUAddUsersAckResult();
            uar.Status = (int)RetStatus.NotValid;

            int deviceId;
            string deviceIp;
            UsersToAdd = UsersToAdd ?? new FpuUserAdd[0];

            if (CheckCredentials(FPUAddUsersAckMethod, out deviceId, out deviceIp))
            {
                return uar;
            }

            Log(FPUAddUsersAckMethod, deviceIp, deviceId, false, string.Format("Started. Templates acknowledged: {0}", UsersToAdd.Length));

            try
            {
                var count = UsersToAdd.GetLength(0);

                if (count == 0)
                {
                    Log(FPUAddUsersAckMethod, deviceIp, deviceId, false, "UsersToAdd[] is null or empty");
                    return uar;
                }

                using (var connection = new SqlConnection(ConnectionString))
                using (var command = connection.CreateCommand())
                {
                    string qry = @"
                        INSERT INTO sy_template_sync_terminals (TemplateID, TerminalID, Sync_Date) 
                        SELECT ID, @terminalId, getdate()
                        FROM sy_templates 
                        WHERE TemplateId = @empId
                          AND ID NOT IN (SELECT TemplateID FROM sy_template_sync_terminals WHERE TerminalID = @terminalId)";

                    command.CommandText = qry;

                    connection.Open();

                    var terminalId = GetTerminalId(FPUAddUsersAckMethod, deviceIp, deviceId);

                    int i;

                    for (i = 0; i < count; i++)
                    {
                        Log(FPUAddUsersAckMethod, deviceIp, deviceId, false, "UsersToAdd[i].ID=[" + UsersToAdd[i].ID + "]");
                        Log(FPUAddUsersAckMethod, deviceIp, deviceId, false, "UsersToAdd[i].TemplateNumber=[" + UsersToAdd[i].TemplateNumber + "]");
                        Log(FPUAddUsersAckMethod, deviceIp, deviceId, false, "UsersToAdd[i].Result=[" + UsersToAdd[i].Result + "]");

                        if (UsersToAdd[i].Result == 0)
                        {
                            var empId = UsersToAdd[i].ID.PadLeft(10, '0');

                            if (!string.IsNullOrWhiteSpace(empId) && (terminalId >= 0))
                            {
                                try
                                {
                                    command.Parameters.Clear();
                                    command.Parameters.AddWithValue("@empId", empId);
                                    command.Parameters.AddWithValue("@terminalId", terminalId);
                                    var rowsAffected = command.ExecuteNonQuery();

                                    Log(FPUAddUsersAckMethod, deviceIp, deviceId, false, string.Format("Synchonized for {0}. {1} rows affected", empId, rowsAffected));
                                }
                                catch (Exception ex)
                                {
                                    Log(FPUAddUsersAckMethod, deviceIp, deviceId, true, string.Format("Error adding sync record to db for {0}: {1}", empId, ex));
                                }
                            }
                            else
                            {
                                Log(FPUAddUsersAckMethod, deviceIp, deviceId, false, "FAILED to get terminal_ID or template_ID");
                            }
                        }
                    }
                }

                uar.Status = (int)RetStatus.Ok;
            }
            catch (Exception ex)
            {
                Log(FPUAddUsersAckMethod, deviceIp, deviceId, true, ex.ToString());
                uar.Status = (int)RetStatus.Error;
            }

            Log(FPUAddUsersAckMethod, deviceIp, deviceId, false, "Done");

            return uar;
        }

        #endregion

        #region -- GetFPUDeleteUsers ----------------------------------------------------

        public new struct User
        {
            public string ID;
        }

        public struct GetFPUDelete
        {
            public User[] Users;
            public Boolean UsersEnd;
        }

        [WebMethod(Description = "GetFPUDeleteUsers - Request deleted fingerprint templates")]
        [SoapDocumentMethod]
        [SoapHeader("Credentials", Direction = SoapHeaderDirection.In)]
        public GetFPUDelete GetFPUDeleteUsers(int PortionNumber)
        {
            //Log(String.Format("GetFPUDeleteUsers() IN: PortionNumber={0}", PortionNumber));

            GetFPUDelete gfd = new GetFPUDelete { UsersEnd = true };

            int deviceId;
            string deviceIp;

            if (CheckCredentials(GetFPUDeleteUsersMethod, out deviceId, out deviceIp))
            {
                return gfd;
            }

            Log(GetFPUDeleteUsersMethod, deviceIp, deviceId, false, string.Format("Started. PortionNumber: {0}", PortionNumber));

            SqlConnection Connection = null;
            SqlDataReader Reader = null;
            SqlCommand Command = null;
            string qry = String.Empty;

            try
            {
                int terminalId;
                int iMaxEmpToSend = 100;
                int iCount = 0;

                terminalId = GetTerminalId(GetFPUDeleteUsersMethod, deviceIp, deviceId);

                if (!int.TryParse(GetString("GetFPUDeleteUsers_iMaxEmpToSend"), out iMaxEmpToSend))
                    iMaxEmpToSend = 100;

                if (iMaxEmpToSend < 1) iMaxEmpToSend = 100;

                //get top(100) templates for a specific terminal that are in 
                //sy_template_sync_terminals table but not in sy_templates table
                //this way we know the user was removed but is still on the terminal
                qry = String.Format("SELECT top({0}) A.* from sy_template_sync_terminals as A " +
                    "WHERE (TemplateId NOT IN " +
                        "(SELECT ID FROM sy_templates AS B) " +
                            "AND (TerminalID = {1})) " +
                    "order by TemplateId", iMaxEmpToSend, terminalId);

                Connection = new SqlConnection(ConnectionString);
                Command = new SqlCommand(qry, Connection);
                Connection.Open();
                Reader = Command.ExecuteReader();

                if (!Reader.HasRows)
                {
                    Log(GetFPUDeleteUsersMethod, deviceIp, deviceId, false, "No templates to delete");

                    if (Command != null) Command.Dispose();
                    if (Reader != null) { Reader.Close(); Reader.Dispose(); }
                    if (Connection != null) { Connection.Close(); Connection.Dispose(); }

                    return gfd;
                }

                List<User> users = new List<User>();

                while (Reader.Read() && (iCount < iMaxEmpToSend))
                {
                    User usr = new User();
                    usr.ID = Reader["TemplateID"].ToString();

                    Log(GetFPUDeleteUsersMethod, deviceIp, deviceId, false, string.Format("{0} will be deleted from the terminal", usr.ID));

                    users.Add(usr);
                    iCount++;
                }

                gfd.Users = users.ToArray();
                gfd.UsersEnd = (iCount < iMaxEmpToSend);
            }
            catch (Exception ex)
            {
                Log(GetFPUDeleteUsersMethod, deviceIp, deviceId, true, ex.ToString());
            }
            finally
            {
                if (Reader != null) Reader.Dispose();
                if (Command != null) Command.Dispose();
                if (Connection != null) { Connection.Close(); Connection.Dispose(); }
            }

            Log(GetFPUDeleteUsersMethod, deviceIp, deviceId, false,
                string.Format("Done. Templates to delete: {0}", string.Join(",", gfd.Users.Select(e => e.ID))));

            return gfd;
        }

        #endregion

        #region -- FPUDeleteUsersAck ----------------------------------------------------

        public struct FpuUserDelete
        {
            public string ID;
            public int Result;
        }

        public struct FPUDeleteUsersAckResult
        {
            public int Status;
        }

        [WebMethod(Description = "FPUDeleteUsersAck - Acknowledge deleted fingerprints")]
        [SoapDocumentMethod]
        [SoapHeader("Credentials", Direction = SoapHeaderDirection.In)]
        public FPUDeleteUsersAckResult FPUDeleteUsersAck(FpuUserDelete[] UsersToDelete)
        {
            FPUDeleteUsersAckResult uar = new FPUDeleteUsersAckResult();
            uar.Status = (int)RetStatus.NotValid;

            SqlConnection conn = null;
            SqlCommand cmd = null;
            SqlParameter param = null;

            int deviceId;
            string deviceIp;

            if (CheckCredentials(FPUDeleteUsersAckMethod, out deviceId, out deviceIp))
            {
                return uar;
            }

            UsersToDelete = UsersToDelete ?? new FpuUserDelete[0];

            Log(FPUDeleteUsersAckMethod, deviceIp, deviceId, false, string.Format("Started. {0} records acknowledged", UsersToDelete.Length));

            try
            {
                var iCount = UsersToDelete.GetLength(0);

                if (iCount < 1)
                {
                    uar.Status = (int)RetStatus.Ok;
                    return uar;
                }

                var terminalId = GetTerminalId(FPUDeleteUsersAckMethod, deviceIp, deviceId);

                string qry = String.Format("delete from sy_template_sync_terminals where TerminalID={0} AND TemplateID=@TemplateToDelete", terminalId);

                conn = new SqlConnection(ConnectionString);
                conn.Open();
                cmd = new SqlCommand(qry, conn);
                param = new SqlParameter();
                param.ParameterName = "@TemplateToDelete";
                param.Value = "0";
                cmd.Parameters.Add(param);

                for (int i = 0; i < iCount; i++)
                {
                    Log(FPUDeleteUsersAckMethod, deviceIp, deviceId, false, "UsersToDelete[i].ID=" + UsersToDelete[i].ID);

                    param.Value = UsersToDelete[i].ID;
                    cmd.ExecuteNonQuery();

                    Log(FPUDeleteUsersAckMethod, deviceIp, deviceId, false, "UsersToDelete[i].ID=" + UsersToDelete[i].ID + " - Success");
                }

                uar.Status = (int)RetStatus.Ok;
            }
            catch (Exception ex)
            {
                Log(FPUDeleteUsersAckMethod, deviceIp, deviceId, true, ex.ToString());
                uar.Status = (int)RetStatus.Error;
            }
            finally
            {
                if (cmd != null) cmd.Dispose();
                if (conn != null) { conn.Close(); conn.Dispose(); }
            }

            Log(FPUDeleteUsersAckMethod, deviceIp, deviceId, false, "Done");

            return uar;
        }

        #endregion

        #region -- CRValidEmployee ---------------------------------------------

        public struct CRValidEmployeeResult
        {
            public int Status;
            public string Name;
            public string NextStep;
            public string id;
            public long ImageSize;
            public string Image;
            public long RecordId;
        }

        [WebMethod(Description = "Check if a card is valid for the given terminal (Class registration)")]
        [SoapHeader("Credentials")]
        public CRValidEmployeeResult CRValidEmployee(string id, DateTime utcTime, int FunctionCode, int InputCode,
            bool GetImage)
        {
            CRValidEmployeeResult mva = new CRValidEmployeeResult();

            mva.Status = (int)RetStatus.NotValid;
            mva.Name = "";
            mva.NextStep = "N";
            mva.id = id;
            mva.ImageSize = 0;
            mva.Image = "";

            SqlConnection Connection = null;
            SqlDataReader Reader = null;

            int deviceId;
            string deviceIp;

            if (CheckCredentials(CRValidEmployeeMethod, out deviceId, out deviceIp))
            {
                return mva;
            }

            Log(CRValidEmployeeMethod, deviceIp, deviceId, false,
                String.Format("id={0}, utcTime={1}, ScanKey={2}, InputCode={3}, GetImage={4}",
                    id, utcTime, FunctionCode, InputCode, GetImage));

            if (String.IsNullOrEmpty(id))
            {
                Log(CRValidEmployeeMethod, deviceIp, deviceId, false, "Id is null or empty");
                return mva;
            }

            try
            {
                var terminalInfo = GetTerminalInfo(CRValidEmployeeMethod, deviceIp, deviceId);

                if (terminalInfo.TerminalUtilization == TerminalUtilization.AccessControl
                    || terminalInfo.TerminalUtilization == TerminalUtilization.RegistrationAndAccessControl)
                {
                    mva.Name = "Not Allowed";
                    short trType = 1;
                    switch (FunctionCode)
                    {
                        case 1://// Scan key = 1 - IN
                            trType = 1;
                            break;
                        case 2://// Scan key = 2 - OUT
                            trType = 2;
                            break;
                        default:
                            break;
                    }
                    using (SqlConnection connection = new SqlConnection(ConnectionString))
                    {
                        connection.Open();
                        using (SqlCommand command = new SqlCommand("[ac_ProcessSY400Query]", connection))
                        {
                            command.CommandType = CommandType.StoredProcedure;

                            command.Parameters.Add("@TerminalIP", SqlDbType.NVarChar).Value = deviceIp;
                            command.Parameters.Add("@CardNumber", SqlDbType.NVarChar).Value = id;
                            command.Parameters.Add("@Time", SqlDbType.Int).Value = utcTime.Hour * 3600 + utcTime.Minute * 60 + utcTime.Second;
                            command.Parameters.Add("@ClockType", SqlDbType.Bit).Value = 0;/// 0 - for query (query.BufferPrefix == Constants.Parser.PrefixQuery) ? 0 : 1;
                            command.Parameters.Add("@TransactionType", SqlDbType.SmallInt).Value = trType;
                            command.Parameters.Add("@TerminalNum", SqlDbType.VarChar, 1).Value = '1';/// 1 =- because all readers for synergy will be 1
                            command.Parameters.Add("@DatePart", SqlDbType.DateTime).Value = utcTime.Date;
                            //// in app it is different

                            //Log("****** InputCodeBefore=[" + InputCode + "]");
                            //Log("****** InputCodeFPU=[" + ACFPUCODE + "]");
                            //Log("****** InputCodePC=[" + ACPCCODE + "]");
                            //Log("****** InputCodePIN=[" + ACPINCODE + "]");
                            if (InputCode == ACFPUCODE) InputCode = 1; ////fingerprint
                            else if (ACPCCODE.Contains(InputCode)) InputCode = 2; ////proximity card
                            else if (InputCode == ACPINCODE) InputCode = 3; ////PIN

                            Log(CRValidEmployeeMethod, deviceIp, deviceId, false, "InputCodeAfterChange=[" + InputCode + "]");
                            command.Parameters.Add("@InputCode", SqlDbType.Int).Value = InputCode;

                            command.Parameters.Add("@IsAllowed", SqlDbType.Bit).Direction = ParameterDirection.Output;
                            command.Parameters.Add("@CheckFinger", SqlDbType.Bit).Direction = ParameterDirection.Output;
                            command.Parameters.Add("@HasError", SqlDbType.Bit).Direction = ParameterDirection.Output;
                            command.Parameters.Add("@EmpType", SqlDbType.VarChar, 1).Direction = ParameterDirection.Output;
                            command.Parameters.Add("@PINCode", SqlDbType.VarChar, 4).Direction = ParameterDirection.Output;
                            command.Parameters.Add("@TemplateId", SqlDbType.VarChar, 10).Direction = ParameterDirection.Output;
                            command.Parameters.Add("@Message", SqlDbType.NVarChar, 250).Direction = ParameterDirection.Output;
                            command.Parameters.Add("@RecordID", SqlDbType.BigInt).Direction = ParameterDirection.Output;

                            command.ExecuteNonQuery();

                            bool hasError = (bool)command.Parameters["@HasError"].Value;
                            //bool checkFinger = (bool)command.Parameters["@CheckFinger"].Value;
                            if (!hasError)
                            {
                                bool isAllowed = (bool)command.Parameters["@IsAllowed"].Value;
                                if (isAllowed)
                                {
                                    mva.Status = (int)RetStatus.Ok;
                                    mva.Name = "Success";
                                }
                            }
                            if (!Convert.IsDBNull(command.Parameters["@RecordID"].Value))
                                mva.RecordId = (long)command.Parameters["@RecordID"].Value;
                        }
                        connection.Close();
                    }

                    ////registration - only when terminal is working for both and result is OK
                    if (terminalInfo.TerminalUtilization == TerminalUtilization.RegistrationAndAccessControl
                        && mva.Status == (int)RetStatus.Ok)
                    {
                        Connection = new SqlConnection(ConnectionString);
                        Connection.Open();

                        SqlCommand cmd = new SqlCommand("stp_Generic_Swipe_Process", Connection);
                        cmd.CommandType = CommandType.StoredProcedure;

                        int EarliestArrival, LateDeparture, LateTolerance, VeryLateTolerance, LessonCode = 0;
                        if (!int.TryParse(GetString("EarliestArrival"), out EarliestArrival)) EarliestArrival = 10;
                        if (!int.TryParse(GetString("LateDeparture"), out LateDeparture)) LateDeparture = 5;
                        if (!int.TryParse(GetString("LateTolerance"), out LateTolerance)) LateTolerance = 15;
                        if (!int.TryParse(GetString("VeryLateTolerance"), out VeryLateTolerance)) VeryLateTolerance = 60;

                        cmd.Parameters.Add(new SqlParameter("@TerminalIP", deviceIp));
                        cmd.Parameters.Add(new SqlParameter("@CardNumber", id));
                        cmd.Parameters.Add(new SqlParameter("@LessonCode", LessonCode));     //not in use
                        cmd.Parameters.Add(new SqlParameter("@Time", utcTime.Hour * 60 + utcTime.Minute));
                        cmd.Parameters.Add(new SqlParameter("@EarliestArrival", EarliestArrival));
                        cmd.Parameters.Add(new SqlParameter("@LateDeparture", LateDeparture));
                        cmd.Parameters.Add(new SqlParameter("@LateTolerance", LateTolerance));
                        cmd.Parameters.Add(new SqlParameter("@VeryLateTolerance", VeryLateTolerance));
                        cmd.Parameters.Add(new SqlParameter("@ScanKey", FunctionCode));
                        cmd.Parameters.Add(new SqlParameter("@InputCode", InputCode));
                        cmd.Parameters.Add("@SwipeDate", SqlDbType.DateTime).Value = utcTime;

                        SqlParameter IsStudent = cmd.Parameters.Add("@IsStudent", SqlDbType.Bit);
                        IsStudent.Value = false;
                        IsStudent.Direction = ParameterDirection.InputOutput;

                        SqlParameter ProcessCode = cmd.Parameters.Add("@ProcessCode", SqlDbType.Int);
                        ProcessCode.Value = 0;
                        ProcessCode.Direction = ParameterDirection.InputOutput;

                        SqlParameter Message = cmd.Parameters.Add("@Message", SqlDbType.NVarChar, 50);
                        Message.Value = "";
                        Message.Direction = ParameterDirection.InputOutput;

                        cmd.Parameters.Add("@RecordID", SqlDbType.BigInt).Direction = ParameterDirection.Output;

                        cmd.ExecuteNonQuery();

                        Log(CRValidEmployeeMethod, deviceIp, deviceId, false,
                            string.Format("IsStudent={0}, ProcessCode={1}, Message=[{2}], RecordID {3}",
                                (bool)IsStudent.Value, (int)ProcessCode.Value, (string)Message.Value,
                                cmd.Parameters["@RecordID"].Value));

                        //// only in name we pass the result because door should be open in any case.
                        mva.Name = (string)Message.Value;
                        if (!Convert.IsDBNull(cmd.Parameters["@RecordID"].Value))
                            mva.RecordId = (long)cmd.Parameters["@RecordID"].Value;

                        Connection.Close();
                    }
                }
                else if (terminalInfo.TerminalUtilization == TerminalUtilization.Registration)
                {
                    Connection = new SqlConnection(ConnectionString);
                    Connection.Open();

                    SqlCommand cmd = new SqlCommand("stp_Generic_Swipe_Process", Connection);
                    cmd.CommandType = CommandType.StoredProcedure;

                    int EarliestArrival, LateDeparture, LateTolerance, VeryLateTolerance, LessonCode = 0;
                    if (!int.TryParse(GetString("EarliestArrival"), out EarliestArrival)) EarliestArrival = 10;
                    if (!int.TryParse(GetString("LateDeparture"), out LateDeparture)) LateDeparture = 5;
                    if (!int.TryParse(GetString("LateTolerance"), out LateTolerance)) LateTolerance = 15;
                    if (!int.TryParse(GetString("VeryLateTolerance"), out VeryLateTolerance)) VeryLateTolerance = 60;

                    if (InputCode == ACFPUCODE) InputCode = 1; ////fingerprint
                    else if (ACPCCODE.Contains(InputCode)) InputCode = 2; ////proximity card
                    else if (InputCode == ACPINCODE) InputCode = 3; ////PIN

                    Log(CRValidEmployeeMethod, deviceIp, deviceId, false, "InputCodeAfterChange=[" + InputCode + "]");

                    cmd.Parameters.Add(new SqlParameter("@TerminalIP", deviceIp));
                    cmd.Parameters.Add(new SqlParameter("@CardNumber", id));
                    cmd.Parameters.Add(new SqlParameter("@LessonCode", LessonCode));     //not in use
                    cmd.Parameters.Add(new SqlParameter("@Time", utcTime.Hour * 60 + utcTime.Minute));
                    cmd.Parameters.Add(new SqlParameter("@EarliestArrival", EarliestArrival));
                    cmd.Parameters.Add(new SqlParameter("@LateDeparture", LateDeparture));
                    cmd.Parameters.Add(new SqlParameter("@LateTolerance", LateTolerance));
                    cmd.Parameters.Add(new SqlParameter("@VeryLateTolerance", VeryLateTolerance));
                    cmd.Parameters.Add(new SqlParameter("@ScanKey", FunctionCode));
                    cmd.Parameters.Add(new SqlParameter("@InputCode", InputCode));
                    cmd.Parameters.Add("@SwipeDate", SqlDbType.DateTime).Value = utcTime;

                    SqlParameter IsStudent = cmd.Parameters.Add("@IsStudent", SqlDbType.Bit);
                    IsStudent.Value = false;
                    IsStudent.Direction = ParameterDirection.InputOutput;

                    SqlParameter ProcessCode = cmd.Parameters.Add("@ProcessCode", SqlDbType.Int);
                    ProcessCode.Value = 0;
                    ProcessCode.Direction = ParameterDirection.InputOutput;

                    SqlParameter Message = cmd.Parameters.Add("@Message", SqlDbType.NVarChar, 50);
                    Message.Value = "";
                    Message.Direction = ParameterDirection.InputOutput;

                    cmd.Parameters.Add("@RecordID", SqlDbType.BigInt).Direction = ParameterDirection.Output;

                    cmd.ExecuteNonQuery();

                    Log(CRValidEmployeeMethod, deviceIp, deviceId, false,
                        string.Format("IsStudent={0}, ProcessCode={1}, Message=[{2}], RecordID {3}",
                            (bool)IsStudent.Value, (int)ProcessCode.Value, (string)Message.Value,
                            cmd.Parameters["@RecordID"].Value));

                    mva.Status = (int)((int)ProcessCode.Value == 1 ? RetStatus.Ok : RetStatus.NotValid);
                    //mva.Status = (int)RetStatus.Ok;

                    mva.Name = (string)Message.Value;
                    mva.NextStep = "N";     //for None. temporary until details verified

                    if (!Convert.IsDBNull(cmd.Parameters["@RecordID"].Value))
                    {
                        Log(CRValidEmployeeMethod, deviceIp, deviceId, false, "RecordId - " + cmd.Parameters["@RecordID"].Value);
                        mva.RecordId = (long)cmd.Parameters["@RecordID"].Value;
                    }
                }

            }
            catch (Exception ex)
            {
                Log(CRValidEmployeeMethod, deviceIp, deviceId, true, ex.ToString());
                mva.Status = (int)RetStatus.Error;
            }
            finally
            {
                if (Reader != null) Reader.Dispose();
                if (Connection != null && Connection.State == ConnectionState.Open) { Connection.Close(); Connection.Dispose(); }
            }

            Log(CRValidEmployeeMethod, deviceIp, deviceId, false,
                string.Format("CRValidEmployee OUT:  mva.id={0}, mva.Name={1}, mva.NextStep={2}, mva.Status={3}",
                    mva.id, mva.Name, mva.NextStep, mva.Status));

            return mva;
        }

        #endregion

        #region -- CRSendTransaction -------------------------------------------

        public struct CRSendTransactionResult
        {
            /*<CRSendTransactionResult>
                <CheckFPU>int</CheckFPU>
                <CheckPinCode>int</CheckPinCode>
                <PinCode>string</PinCode>
                <Employee>string</Employee>
                <Id>int</Id>
                <Status>int</Status>
                <Message>string</Message>
                <HostTime>dateTime</HostTime>
                <image>string</image>
              </CRSendTransactionResult>
             */
            public int CheckFPU;
            public int CheckPinCode;
            public string PinCode;
            public string Employee;
            public int Id;
            public int Status;
            public string Message;
            public DateTime HostTime;
            public string image;
        }

        [WebMethod(Description = "Save class registration")]
        [SoapHeader("Credentials")]
        public CRSendTransactionResult CRSendTransaction(string id, DateTime utcTime, int FunctionCode, int InputCode, int Online, string Budget, string subBudget,
            string Restaurant, string Image, string ServiceType, string KeyCode, string DailyCounter, string MonthlyCounter, string Amount)
        {

            CRSendTransactionResult res = new CRSendTransactionResult();
            //// for now
            res.CheckFPU = 0;
            res.CheckPinCode = 0;
            res.PinCode = string.Empty;
            res.Employee = id.PadLeft(10, '0');
            res.Id = 1;//Int32.Parse(id);
            res.image = string.Empty;
            //// end for now

            if (Online != 0)        //if online do nothing. The work was done in CRValidEmployee()
            {
                res.Status = (int)RetStatus.Ok;
                res.Message = "OK";
            }

            string deviceIp;
            int deviceId;

            if (CheckCredentials(CRSendTransactionMethod, out deviceId, out deviceIp))
            {
                return res;
            }

            Log(CRSendTransactionMethod, deviceIp, deviceId, false,
                String.Format(
                    "CRSendTransactionResult IN: id={0}, utcTime={1}, FunctionCode={2}, InputCode={3}, Image={4}, Online={5}, Budget={6}, subBudget={7}, Restaurant={8}, " +
                    "ServiceType={9}, KeyCode={10}, DailyCounter={11}, MonthlyCounter={12}, Amount={13}",
                    id, utcTime, FunctionCode, InputCode, Image, Online, Budget, subBudget, Restaurant, ServiceType,
                    KeyCode, DailyCounter, MonthlyCounter, Amount));


            //if offline, go do the save thru CRValidEmployee().
            CRValidEmployeeResult mva = CRValidEmployee(id, utcTime, FunctionCode, InputCode, false);

            try
            {
                if (ServiceType.ToLower().Equals("query"))
                {
                    if (mva.RecordId > 0 && !string.IsNullOrEmpty(Image))
                    {
                        using (SqlConnection conn = new SqlConnection(ConnectionString))
                        {
                            string qry = String.Format("INSERT INTO [dbo].[sy_ac_additional] ([RecID], [ImageText]) VALUES ({0}, '{1}')", mva.RecordId, Image);
                            SqlCommand Command = new SqlCommand(qry, conn);
                            conn.Open();
                            Command.ExecuteReader();
                            conn.Close();
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Log(CRSendTransactionMethod, deviceIp, deviceId, true, ex.ToString());
            }

            res.Status = (int)mva.Status;
            res.Message = mva.Name;
            res.HostTime = DateTime.Now;

            Log(CRSendTransactionMethod, deviceIp, deviceId, false,
                string.Format("Done. Status: {0}, Processing result: {1}", res.Status, res.Message));

            return res;
        }

        #endregion

        #region -- GetAppFilesAndTables -------------------------------------------

        public struct file
        {
            public string Name;
            public string Base64;
        }

        public struct ColumnNames
        {
            public string[] ColumnName;
        }

        public struct Row
        {
            public string[] Data;//Column[] 
        }

        public struct GetAppFilesAndTablesResult
        {
            public ColumnNames ColumnNames;
            public Row[] rows;
            public file[] files;
        }

        [WebMethod(Description = "GetAppFilesAndTables - return employees for offline punches")]
        [SoapDocumentMethod]
        [SoapHeader("Credentials", Direction = SoapHeaderDirection.In)]
        public GetAppFilesAndTablesResult GetAppFilesAndTables(string File_TableName, bool Update = false, bool Isdelete = false, bool Acknowledge = false, string sParams = "")
        {
            GetAppFilesAndTablesResult rows = new GetAppFilesAndTablesResult();

            int deviceId = 0;
            string deviceIp;

            if (CheckCredentials(GetAppFilesAndTablesMethod, out deviceId, out deviceIp))
            {
                return rows;
            }

            Log(GetAppFilesAndTablesMethod, deviceIp, deviceId, false,
                string.Format("Started. Table name: {0}, Update: {1}, Isdelete: {2}, Acknowledge: {3}, sParams: {4}",
                    File_TableName, Update, Isdelete,
                    Acknowledge, sParams));

            try
            {
                var terminalId = GetTerminalId(GetAppFilesAndTablesMethod, deviceIp, deviceId);
                string rdyFolder = GetString("RdyFolder");

                if (string.IsNullOrEmpty(rdyFolder))
                {
                    Log(GetAppFilesAndTablesMethod, deviceIp, deviceId, false, "No terminal folder");
                    return rows;
                }

                string dir = Path.Combine(rdyFolder, string.Format("terminal_{0}_{1}", terminalId.ToString(), deviceIp.Replace(".", "")));

                List<file> f = new List<file>();
                string sBase64 = "";
                file f1 = new file();
                FileStream fs = new FileStream(Path.Combine(dir, "Emp.xml"), FileMode.Open, FileAccess.Read);
                byte[] filebytes = new byte[fs.Length];
                fs.Read(filebytes, 0, Convert.ToInt32(fs.Length));
                sBase64 = Convert.ToBase64String(filebytes, Base64FormattingOptions.InsertLineBreaks);
                f1.Base64 = sBase64;
                f.Add(f1);
                rows.files = f.ToArray();
                fs.Close();
                fs.Dispose();
                fs = null;

                //// marking it as sent
                using (SqlConnection conn = new SqlConnection(ConnectionString))
                {
                    conn.Open();
                    SqlCommand cmd = new SqlCommand("UPDATE sy_terminals SET NeedSync=0 WHERE ID=@ID", conn);
                    cmd.Parameters.Add("@ID", SqlDbType.Int).Value = terminalId;
                    cmd.ExecuteNonQuery();
                    conn.Close();
                }
            }
            catch (Exception ex)
            {
                Log(GetAppFilesAndTablesMethod, deviceIp, deviceId, true, ex.ToString());
            }

            Log(GetAppFilesAndTablesMethod, deviceIp, deviceId, false, "Done");

            return rows;
        }

        #endregion

        #region -- CRGetEmployeeTable AccessControl ---------------------------------------------

        public struct CREmployee
        {
            public string ID;
            public string Name;
            public string NextStep;
            public string PIN;
            public string CanSwipeOut;
        }

        public struct CRGetEmployeeTableResult
        {
            public CREmployee[] Employees;
        }

        [WebMethod(Description = "Check if a card is valid for the given terminal (Class registration)")]
        [SoapHeader("Credentials")]
        public CRGetEmployeeTableResult CRGetEmployeeTable()
        {
            CRGetEmployeeTableResult mva = new CRGetEmployeeTableResult();

            int deviceId;
            string deviceIp;

            if (CheckCredentials(CRGetEmployeeTableMethod, out deviceId, out deviceIp))
            {
                return mva;
            }

            Log(CRGetEmployeeTableMethod, deviceIp, deviceId, false, "Started");

            try
            {
                DateTime curDate = DateTime.Now;
                var terminalInfo = GetTerminalInfo(CRGetEmployeeTableMethod, deviceIp, deviceId);

                if (terminalInfo.TerminalUtilization == TerminalUtilization.AccessControl)
                {
                    Log(CRGetEmployeeTableMethod, deviceIp, deviceId, false, "AC terminal");

                    using (SqlConnection connection = new SqlConnection(ConnectionString))
                    {
                        connection.Open();
                        using (SqlCommand command = new SqlCommand("[ac_CRGetEmployeeTable]", connection))
                        {
                            command.CommandType = CommandType.StoredProcedure;

                            command.Parameters.Add("@TerminalIP", SqlDbType.NVarChar).Value = deviceIp;
                            command.Parameters.Add("@TimePart", SqlDbType.Int).Value = curDate.Hour * 60 + curDate.Minute;
                            command.Parameters.Add("@DatePart", SqlDbType.DateTime).Value = curDate.Date;

                            SqlDataReader reader = command.ExecuteReader();

                            if (reader.HasRows)
                            {
                                Log(CRGetEmployeeTableMethod, deviceIp, deviceId, false, "Building list of employees");
                                List<CREmployee> emps = new List<CREmployee>();

                                while (reader.Read())
                                {
                                    CREmployee emp = new CREmployee();
                                    emp.ID = reader.GetString(0);
                                    emp.Name = reader.GetString(1);
                                    emp.NextStep = "N";
                                    emp.PIN = (reader.IsDBNull(2)) ? string.Empty : reader.GetString(2);
                                    emp.CanSwipeOut = reader.GetString(3);
                                    emps.Add(emp);
                                }

                                mva.Employees = emps.ToArray();

                                Log(CRGetEmployeeTableMethod, deviceIp, deviceId, false,
                                    string.Format("Employees found: {0}", mva.Employees.Length.ToString()));
                            }
                            else
                            {
                                Log(CRGetEmployeeTableMethod, deviceIp, deviceId, true, "No employees were retrieved");
                            }
                        }
                        connection.Close();
                    }
                }
                else
                {
                    Log(CRGetEmployeeTableMethod, deviceIp, deviceId, true, "Not an AC terminal");
                }
            }
            catch (Exception ex)
            {
                Log(CRGetEmployeeTableMethod, deviceIp, deviceId, true, ex.ToString());
            }

            Log(CRGetEmployeeTableMethod, deviceIp, deviceId, false, "Done");

            return mva;
        }

        #endregion

        #region -- Enrolling Finger ----------------------------------------------------

        public struct SendFPUTemplateResult
        {
            public int Status;
            public string Message;
        }

        [WebMethod(Description = "SendFPUTemplate - Enroll new fingerprint template")]
        [SoapDocumentMethod]
        [SoapHeader("Credentials", Direction = SoapHeaderDirection.In)]
        public int SendFPUTemplate(string Employee, string Template_Base64, int FingerNo)
        {
            int status = 0;
            int deviceId;
            string deviceIp;

            if (CheckCredentials(SendFPUTemplateMethod, out deviceId, out deviceIp))
            {
                return status;
            }

            Log(SendFPUTemplateMethod, deviceIp, deviceId, false,
                string.Format("Started Employee:{0}, Finger_No:{1}", Employee, FingerNo));

            try
            {
                var terminalId = GetTerminalId(SendFPUTemplateMethod, deviceIp, deviceId);
                var empId = Employee.PadLeft(10, '0');
                var deviceFpuType = GetTerminalFPUType(SendFPUTemplateMethod, deviceIp, deviceId);

                string qry = @"
                        if (select count(*) from [dbo].[sy_templates] where [TemplateId] = @employeeId AND [FingerNum] = @fingerId) = 0
                        begin
	                        insert into [dbo].[sy_templates] ([TemplateId],[Template], [Status], [FingerNum], [TemplateType], [TemplateIndex], [TemplateStoredAs], [TemplateQuality])
	                        values (@employeeId, @template, 1, @fingerId, @templateType, null, 2, null)	

	                        declare @templateId int = (select @@IDENTITY)

	                        insert into [sy_template_sync_terminals] ([TemplateID],[TerminalID],[Sync_Date]) values (@templateId, @terminalId, getdate())
                        end
                        else
                        begin
	                        update [dbo].[sy_templates] 
	                        set [Template] = @template,
     	                        [TemplateType] = @TemplateType, 
	                            [TemplateStoredAs] = 2 
	                        where [TemplateId] = @employeeId AND [FingerNum] = @fingerId 
                        end";

                using (SqlConnection connection = new SqlConnection(ConnectionString))
                using (var command = new SqlCommand(qry, connection))
                {
                    connection.Open();
                    command.Parameters.AddWithValue("@employeeId", empId);
                    command.Parameters.AddWithValue("@fingerId", FingerNo);
                    command.Parameters.AddWithValue("@templateType", deviceFpuType == 5 ? 3 : 2); // if biosmack then 3, otherwise - Suprema
                    command.Parameters.AddWithValue("@template", Template_Base64);
                    command.Parameters.AddWithValue("@terminalId", terminalId);
                    command.ExecuteNonQuery();
                }
            }
            catch (Exception ex)
            {
                Log(SendFPUTemplateMethod, deviceIp, deviceId, true, ex.ToString());
            }

            Log(SendFPUTemplateMethod, deviceIp, deviceId, false, string.Format("Done. Status: {0}", status));

            return status;
        }

        #endregion


    }
}
