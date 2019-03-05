using System;
using System.Collections.Generic;
using System.Web;
using System.Xml;
using System.Xml.Serialization;
using System.Web.Services.Protocols;


namespace WebService
{
    public class ClockCredentials : SoapHeader
    {
        private string _ClockId;
        private string _Account;
        private string _MAC;
        private string _ClockIP;

        public string ClockId
        {
            get { return _ClockId; }
            set { _ClockId = value; }
        }
        public string Account
        {
            get { return _Account; }
            set { _Account = value; }
        }
        public string MAC
        {
            get { return _MAC; }
            set { _MAC = value; }
        }

        public string ClockIP
        {
            get { return _ClockIP; }
            set { _ClockIP = value; }
        }
    }
}
