namespace com.blueboxmoon.FreePBX.RestClasses
{
    internal class CallRecord
    {
        public string id { get; set; }

        public string starttime { get; set; }

        public string endtime { get; set; }

        public int duration { get; set; }

        public string src { get; set; }

        public string src_name { get; set; }

        public string dst { get; set; }

        public string dst_name { get; set; }

        public string direction { get; set; }
    }
}
