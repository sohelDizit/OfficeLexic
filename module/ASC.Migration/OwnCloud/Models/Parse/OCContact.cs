﻿using System.Collections.Generic;

namespace ASC.Migration.OwnCloud.Models
{
    public class OCContact
    {
        public string ContactName { get; set; }
        public string Address { get; set; }
        public string Description { get; set; }

        public List<string> Emails { get; set; }
        public List<string> Phones { get; set; }
    }
}
