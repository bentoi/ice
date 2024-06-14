//
// Copyright (c) ZeroC, Inc. All rights reserved.
//

#include "ReapThread.h"
#include "Ice/Ice.h"

using namespace std;
using namespace IceGrid;

ReapThread::ReapThread()
    : _closeCallback([this](const auto& con) { connectionClosed(con); }),
      _terminated(false),
      _thread([this] { run(); })
{
    _wakeInterval = 30s;
}

void
ReapThread::run()
{
    while (true)
    {
        {
            unique_lock lock(_mutex);
            if (_terminated)
            {
                break;
            }

            _condVar.wait_for(lock, _wakeInterval);

            if (_terminated)
            {
                break;
            }

            auto p = _sessions.begin();
            while (p != _sessions.end())
            {
                if (p->item->isDestroyed())
                {
                    //
                    // Remove the reapable
                    //
                    if (p->connection)
                    {
                        auto q = _connections.find(p->connection);
                        if (q != _connections.end())
                        {
                            q->second.erase(p->item);
                            if (q->second.empty())
                            {
                                p->connection->setCloseCallback(nullptr);
                                _connections.erase(q);
                            }
                        }
                    }
                    p = _sessions.erase(p);
                }
                else
                {
                    ++p;
                }
            }
        }
    }
}

void
ReapThread::terminate()
{
    list<ReapableItem> reap;
    {
        lock_guard lock(_mutex);
        if (_terminated)
        {
            assert(_sessions.empty());
            return;
        }
        _terminated = true;
        _condVar.notify_one();
        reap.swap(_sessions);

        for (const auto& conn : _connections)
        {
            conn.first->setCloseCallback(nullptr);
        }
        _connections.clear();
        _closeCallback = nullptr;
    }

    for (const auto& r : reap)
    {
        r.item->destroy(true);
    }
}

void
ReapThread::join()
{
    _thread.join();
}

void
ReapThread::add(const shared_ptr<Reapable>& reapable, const shared_ptr<Ice::Connection>& connection)
{
    lock_guard lock(_mutex);
    if (_terminated)
    {
        return;
    }

    _sessions.push_back({reapable, connection});

    if (connection)
    {
        auto p = _connections.find(connection);
        if (p == _connections.end())
        {
            p = _connections.insert({connection, {}}).first;
            connection->setCloseCallback(_closeCallback);
        }
        p->second.insert(reapable);
    }
}

void
ReapThread::connectionClosed(const shared_ptr<Ice::Connection>& con)
{
    lock_guard lock(_mutex);

    auto p = _connections.find(con);
    if (p == _connections.end())
    {
        con->setCloseCallback(nullptr);
        return;
    }

    for (const auto& reapable : p->second)
    {
        reapable->destroy(false);
    }
    _connections.erase(p);
}
