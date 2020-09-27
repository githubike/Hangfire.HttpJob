using System;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using Hangfire.HttpJob.Support;
using Hangfire.States;
using Hangfire.Storage;

namespace Hangfire.HttpJob.Server
{
    /// <summary>
    /// 处理jobagent通过storage上报的消息
    /// </summary>
    public class JobAgentServer
    {
        /// <summary>
        /// 每隔2s获取一次
        /// </summary>
        private static readonly System.Threading.Timer mDetectionTimer  = new System.Threading.Timer(OnVerify, null, 1000 * 2, 1000 * 2);

        private static string keyPrefix = "_agent_result_";

        private static void OnVerify(object state)
        {
            mDetectionTimer.Change(-1, -1);
            try
            {
                using (var connection = JobStorage.Current.GetConnection())
                {
                    //拿到有上报的jobId集合
                    var jobIdList = connection.GetAllItemsFromSet(keyPrefix);

                    if (!jobIdList.Any()) return;

                    foreach (var jobId in jobIdList)
                    {
                        JobData jobData = connection.GetJobData(jobId);

                        //拿到真正的运行结果
                        var hashKey = keyPrefix + jobId;
                        var result = connection.GetAllEntriesFromHash(hashKey);
                        using (var tran = connection.CreateWriteTransaction())
                        {
                            //job已经不存在了 就直接删除set 
                            if (jobData == null)
                            {
                                tran.RemoveFromSet(keyPrefix, jobId);
                                tran.Commit();
                                continue;
                            }

                            double totalMilliseconds = (DateTime.UtcNow - jobData.CreatedAt).TotalMilliseconds;
                            long latency = (long) totalMilliseconds;

                            //如果job存在 但是没有拿到hash数据 认为成功
                            if (!result.Any())
                            {
                                tran.SetJobState(jobId, new SucceededState(jobId, latency, latency));
                                tran.RemoveFromSet(keyPrefix, jobId);
                                tran.RemoveHash(hashKey);
                                tran.Commit();
                                continue;
                            }

                            var resultOfAgent = result.First();
                            JobAgentResult resultData = CodingUtil.FromJson<JobAgentResult>(resultOfAgent.Value);

                            //异常数据 认为成功
                            if (resultData == null)
                            {
                                tran.SetJobState(jobId, new SucceededState(jobId, latency, latency));
                                tran.RemoveFromSet(keyPrefix, jobId);
                                tran.RemoveHash(hashKey);
                                tran.Commit();
                                continue;
                            }

                            //jobagent实际上运行的时长
                            long.TryParse(resultOfAgent.Key, out var realTotalMilliseconds);
                            if (realTotalMilliseconds < 1) realTotalMilliseconds = latency;
                            var isSuccess = resultData.R == "ok";
                            tran.RemoveFromSet(keyPrefix, jobId);
                            tran.RemoveHash(hashKey);
                            
                            // latency 代表的是 从开始调度 到 实际结束 总共的时长
                            // realTotalMilliseconds 代表的是 jobagent开始执行 到 实际结束的 总共的时长
                            if (isSuccess)
                            {
                                tran.SetJobState(jobId, new SucceededState(jobId, latency, realTotalMilliseconds));
                            }
                            else
                            {
                                tran.SetJobState(jobId, new ErrorState(resultData.E,"JobAgent"));
                            }
                            tran.Commit();
                        }
                    }
                }
            }
            catch
            {
                // ignored
            }
            finally
            {
                mDetectionTimer.Change(1000 * 2, 1000 * 2);
            }
        }
    }
}