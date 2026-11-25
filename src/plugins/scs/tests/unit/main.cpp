// Unit tests of the plugin core. Usage: bbs_scs_tests <path to protocol/vectors.txt>. Exit code 0 = all passed.

#include "test_util.h"

#include <cstdio>

namespace t
{

namespace
{

Test *g_first = nullptr;
Test *g_last = nullptr;
int g_failures = 0;
const char *g_vectors = nullptr;

} // namespace

Registrar::Registrar(Test &test)
{
    if (g_last == nullptr)
    {
        g_first = &test;
    }
    else
    {
        g_last->next = &test;
    }
    g_last = &test;
}

void fail(const char *file, int line, const char *expression)
{
    ++g_failures;
    std::printf("FAIL %s:%d: %s\n", file, line, expression);
}

const char *vectors_path()
{
    return g_vectors;
}

} // namespace t

int main(int argc, char **argv)
{
    if (argc != 2)
    {
        std::printf("usage: bbs_scs_tests <vectors.txt>\n");
        return 2;
    }
    t::g_vectors = argv[1];

    int count = 0;
    for (t::Test *test = t::g_first; test != nullptr; test = test->next)
    {
        const int before = t::g_failures;
        test->run();
        ++count;
        std::printf("%s %s\n", t::g_failures == before ? "ok  " : "FAIL", test->name);
    }
    if (count == 0)
    {
        std::printf("FAIL: no tests registered\n");
        return 1;
    }
    std::printf("%s (%d tests, %d failure(s))\n", t::g_failures == 0 ? "PASS" : "FAIL", count, t::g_failures);
    return t::g_failures == 0 ? 0 : 1;
}
