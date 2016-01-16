﻿using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System.Net;

namespace TestApplication_RAWSerialization.Tests
{
    [TestClass]
    public class PrimitiveTypes
    {

        [TestMethod]
        public void TestInt()
        {
            CookieContainer cookie = new CookieContainer();
            string url1 = string.Format(TestApplication_RAW_Helpers.BASE_URL,
                TestApplication_RAW_Helpers.CONTROLLER, "SetIntVal");
            string url2 = string.Format(TestApplication_RAW_Helpers.BASE_URL,
                TestApplication_RAW_Helpers.CONTROLLER, "GetIntVal");
            
            string result = TestApplication_RAW_Helpers.DoRequest(url1, cookie);
            string result2 = TestApplication_RAW_Helpers.DoRequest(url2, cookie);
            StringAssert.Contains(result, "<sessionVal>3</sessionVal>");
            StringAssert.Contains(result2, "<sessionVal>3</sessionVal>");
        }

        [TestMethod]
        public void TestString()
        {
            CookieContainer cookie = new CookieContainer();
            string url1 = string.Format(TestApplication_RAW_Helpers.BASE_URL,
                TestApplication_RAW_Helpers.CONTROLLER, "SetStringVal");
            string url2 = string.Format(TestApplication_RAW_Helpers.BASE_URL,
                TestApplication_RAW_Helpers.CONTROLLER, "GetStringVal");

            string result1 = TestApplication_RAW_Helpers.DoRequest(url1, cookie);
            string result2 = TestApplication_RAW_Helpers.DoRequest(url2, cookie);
            StringAssert.Contains(result1, "<sessionVal>Barcelona</sessionVal>");
            StringAssert.Contains(result2, "<sessionVal>Barcelona</sessionVal>");
        }        
    }
}
