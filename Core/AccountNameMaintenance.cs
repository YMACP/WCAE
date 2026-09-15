using System;
using System.Collections.Generic;
using System.Threading;

namespace WCAE
{
    public static class AccountNameMaintenance
    {
        public static Dictionary<string,string> Repair(ArticleRepository repository, SessionVault vault,
            Dictionary<string,AccountSession> sessions, CancellationToken cancellation = default)
        {
            cancellation.ThrowIfCancellationRequested();
            var names=repository.RepairAccountNames(cancellation);
            bool changed=false;
            foreach(var pair in sessions)
            {
                cancellation.ThrowIfCancellationRequested();
                if(pair.Value==null)continue;
                names.TryGetValue(pair.Key,out var cachedName);
                var name=AccountNameResolver.Choose(pair.Key,cachedName,pair.Value.Name);
                if(name.Length>0 && name!=cachedName)
                {
                    repository.SaveAccount(pair.Key,name);
                    names[pair.Key]=name;
                }
                if(pair.Value.Name==name)continue;
                pair.Value.Name=name; changed=true;
            }
            if(changed)vault.Save(sessions);
            return names;
        }
    }
}
