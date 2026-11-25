// Tiny test runner: TEST(name) registers a function, CHECK(cond) records a failure with file and line. No framework.
#pragma once

namespace t
{

struct Test
{
    const char *name;
    void (*run)();
    Test *next;
};

struct Registrar
{
    explicit Registrar(Test &test);
};

void fail(const char *file, int line, const char *expression);

// Path of protocol/vectors.txt (argv[1]).
const char *vectors_path();

} // namespace t

#define CHECK(condition)                                                                                               \
    do                                                                                                                 \
    {                                                                                                                  \
        if (!(condition))                                                                                              \
        {                                                                                                              \
            ::t::fail(__FILE__, __LINE__, #condition);                                                                 \
        }                                                                                                              \
    } while (0)

#define TEST(name)                                                                                                     \
    static void name();                                                                                                \
    static ::t::Test name##_test{#name, name, nullptr};                                                                \
    static ::t::Registrar name##_registrar(name##_test);                                                               \
    static void name()
