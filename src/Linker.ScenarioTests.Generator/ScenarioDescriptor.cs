﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Linker.ScenarioTests.Generator
{
    public class ScenarioDescriptor
    {
        public string ClassNamespace { get; set; }
        public string ClassName { get; set; }

        public string MethodName { get; set; }
        public bool IsAsync { get; set; }

        public List<ScenarioInvocationDescriptor> Invocations { get; set; }
    }
}
