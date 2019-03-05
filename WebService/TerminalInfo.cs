using System;
using System.Collections.Generic;
using System.Linq;
using System.Web;

namespace WebService
{
    public class TerminalInfo
    {
        public TerminalInfo()
        {
            Id = -1;
            TerminalUtilization = TerminalUtilization.Registration;
        }

        public int Id { get; set; }
        public string TimezoneId { get; set; }
        public TerminalUtilization TerminalUtilization { get; set; }
    }

    public enum TerminalUtilization
    {
        Registration = 1,
        AccessControl = 2,
        RegistrationAndAccessControl = 3
    }
}