using System;

namespace Checkmarx.API.SAST
{
    public class QueuedScan
    {
        public int id { get; set; }
        public Stage stage { get; set; }
        public string stageDetails { get; set; }
        public object stepDetails { get; set; }
        public Project project { get; set; }
        public Engine? engine { get; set; }
        public Language[] languages { get; set; }
        public string teamId { get; set; }
        public DateTime? dateCreated { get; set; }
        public DateTime? queuedOn { get; set; }
        public DateTime? engineStartedOn { get; set; }
        public DateTime? completedOn { get; set; }
        public int loc { get; set; }
        public bool isIncremental { get; set; }
        public bool isPublic { get; set; }
        public string origin { get; set; }
        public int queuePosition { get; set; }
        public int totalPercent { get; set; }
        public int stagePercent { get; set; }
        public string initiator { get; set; }
    }

    public class Stage
    {
        public int id { get; set; }
        public string value { get; set; }
    }

    public class Project
    {
        public int id { get; set; }
        public string name { get; set; }
        public Link link { get; set; }
    }

    public class Link
    {
        public string rel { get; set; }
        public string uri { get; set; }
    }

    public class Engine
    {
        public int id { get; set; }
        public Link1 link { get; set; }
    }

    public class Link1
    {
        public string rel { get; set; }
        public string uri { get; set; }
    }

    public class Language
    {
        public int id { get; set; }
        public string name { get; set; }
    }
}
