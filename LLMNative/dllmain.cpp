// dllmain.cpp : Defines the entry point for the DLL application.
#include "pch.h"

#include <llama.h>
#include <string>
#include <vector>
#include <queue>
#include <thread>
#include <mutex>
#include <condition_variable>
#include <functional>
#include <optional>

#include <windows.h>
#include <sstream>
#include <iostream>
#include <fstream>

using uint = uint32_t;
#pragma optimize("", off)

//srw locks are cool
struct lock
{
    lock()
    {
        InitializeSRWLock(&inner_);
    }

    SRWLOCK inner_;
};

struct write_scope
{
    write_scope() = delete;
    write_scope(lock& lock)
        : inner_(&lock.inner_)
    {
        AcquireSRWLockExclusive(inner_);
    }

    ~write_scope()
    {
        ReleaseSRWLockExclusive(inner_);
    }
    PSRWLOCK inner_;
};

struct read_scope
{
    read_scope() = delete;
    read_scope(lock& lock)
        : inner_(&lock.inner_)
    {
        AcquireSRWLockShared(inner_);
    }

    ~read_scope()
    {
        ReleaseSRWLockShared(inner_);
    }
    PSRWLOCK inner_;
};

//todo: ref count userdata or put them in a preallocated pool, right now i just leak shit because idgaf its a mod.
#if 0
struct refcnt_t
{
    virtual ~refcnt_t() {}

    void release()
    {
        if (--refcnt_ == 0)
        {
            delete this;
        }
    }

    void add_ref() { refcnt_++; }

    std::atomic<uint> refcnt_ = 0;
};
#endif

struct scheduler
{
    using entrypoint_t = std::function<void(uintptr_t)>;
    using cv_t = CONDITION_VARIABLE;
    struct job_item
    {
        uintptr_t userdata;
        entrypoint_t entrypoint;
    };

    void start()
    {
        running_ = true;
        InitializeConditionVariable(&cv_);

        t_ = std::thread(&scheduler::run, this);
    }

    void stop()
    {
        running_ = false;
        //Notify our thread(s)
        WakeAllConditionVariable(&cv_);
        if (t_.joinable())
            t_.join();

        auto itr = jobs_.begin();
        while (itr != jobs_.end())
        {
            itr = jobs_.erase(itr);
        }

    }

    std::optional<job_item> get_next()
    {
        write_scope _(lock_);
        std::optional<job_item> ret = std::nullopt;

        if (!jobs_.empty())
        {
            ret = jobs_.front();
            jobs_.pop_front();
        }

        return ret;
    }

    //Parks the thread until a job becomes available or we get told to shutdown
    void park_thread()
    {
        read_scope _(lock_);
        while (jobs_.empty() && running_)
        {
            SleepConditionVariableSRW(&cv_, &lock_.inner_, INFINITE, 0);
        }
    }

    void run()
    {
        while (running_)
        {
            park_thread();
            if (!running_)
                break;

            if (auto j = get_next(); j.has_value())
            {
                j->entrypoint(j->userdata);
            }
        }
    }

    void schedule(const job_item& job)
    {
        //Push job into the back of the queue
        {
            write_scope _(lock_);
            if (!running_)
                start();
            jobs_.push_back(job);
        }
        //Notify our thread(s) that there's a job to be done.
        WakeAllConditionVariable(&cv_);
    }

    lock lock_;
    cv_t cv_;
    std::deque<job_item> jobs_;

    std::thread t_;
    bool running_ = false;
} g_scheduler;


template<typename U, typename E>
void schedule(U* ud, E&& entrypoint)
{
    // Create job_item with the given userdata and entrypoint
    scheduler::job_item j{
        reinterpret_cast<uintptr_t>(ud),
        [entrypoint = std::forward<E>(entrypoint)](uintptr_t userdata) {
            U* req = reinterpret_cast<U*>(userdata);
            entrypoint(req); 
        }
    };

    g_scheduler.schedule(j);
}

void static_init_if_needed()
{
    static std::atomic<bool> s_once = false;
    if (s_once.exchange(true) == false)
    {
        ggml_backend_load_all();
    }
}

struct loaded_model
{
    const uint signature = 0xdeaddead;
    lock model_lock_;
    llama_model* inner_ = nullptr;
};

struct load_model_req
{
    loaded_model* dst_ = nullptr;
    std::string path_;
    int ngl_ = 99;
};

void load_model(load_model_req* req)
{
    //todo: parametrize.
    const int ngl = req->ngl_;

    static_init_if_needed();
    
    const char* model_path = req->path_.c_str();

    // initialize the model
    llama_model_params model_params = llama_model_default_params();
    model_params.n_gpu_layers = ngl;

    llama_model* inner = llama_model_load_from_file(model_path, model_params);
    if (!inner) 
    {
        fprintf(stderr, "%s: error: unable to load model\n", __func__);
        return;
    }

    write_scope _(req->dst_->model_lock_);
    req->dst_->inner_ = inner;
}

loaded_model* load_model_async(const char* model_path, const int ngl)
{
    loaded_model* model = new loaded_model();
    load_model_req* req = new load_model_req();
    req->dst_ = model;
    req->path_ = model_path;
    req->ngl_ = ngl;

    schedule(req, load_model);

    return model;
}

struct token_response
{
    const uint signature = 0xdeadbeef;

    lock lock_;
    enum : uint8_t
    {
        Generating = 0,
        Success = 1,
        Failure = 2
    } status_ = Generating;

    char* result_ = nullptr;
};

struct gen_token_req
{
    loaded_model* model_ = nullptr;
    std::string prompt_;
    int n_ctx = 2048;

    token_response* response_;
};

void generate_token(gen_token_req* req)
{
    const int n_ctx = req->n_ctx;

    // initialize the context
    llama_context_params ctx_params = llama_context_default_params();
    ctx_params.n_ctx = n_ctx;
    ctx_params.n_batch = n_ctx;

    std::string response;

    llama_model* model = nullptr;
    {
        read_scope _(req->model_->model_lock_);
        model = req->model_->inner_;
    }

    //todo: is this stupid? we reschedule if the model isnt loaded but we could just end up with the thread getting stuck on a bunch of crap
    if (model == nullptr)
    {
        schedule(req, generate_token);
        return;
    }

    //this is all shameless copy pasta from llama

    llama_context* ctx = llama_init_from_model(model, ctx_params);
    if (!ctx) 
    {
        fprintf(stderr, "%s: error: failed to create the llama_context\n", __func__);
        //return nullptr;
    }

    // initialize the sampler
    llama_sampler* smpl = llama_sampler_chain_init(llama_sampler_chain_default_params());
    llama_sampler_chain_add(smpl, llama_sampler_init_min_p(0.05f, 1));
    llama_sampler_chain_add(smpl, llama_sampler_init_temp(0.8f));
    llama_sampler_chain_add(smpl, llama_sampler_init_dist(LLAMA_DEFAULT_SEED));

    //todo: move this into a top level user session construct that allows the model to have some sort of continuous seeding of prompts.
    std::vector<llama_chat_message> messages;

    messages.push_back({ "user", _strdup(req->prompt_.c_str()) });
    const char* tmpl = llama_model_chat_template(model, /* name */ nullptr);
    std::vector<char> formatted(llama_n_ctx(ctx));
    int prev_len = 0;
    int new_len = llama_chat_apply_template(tmpl, messages.data(), messages.size(), true, formatted.data(), formatted.size());
    if (new_len > (int)formatted.size()) 
    {
        formatted.resize(new_len);
        new_len = llama_chat_apply_template(tmpl, messages.data(), messages.size(), true, formatted.data(), formatted.size());
    }

    // remove previous messages to obtain the prompt to generate the response
    std::string prompt(formatted.begin() + prev_len, formatted.begin() + new_len);

    const llama_vocab* vocab = llama_model_get_vocab(model);

    const bool is_first = llama_get_kv_cache_used_cells(ctx) == 0;

    // tokenize the prompt
    const int n_prompt_tokens = -llama_tokenize(vocab, prompt.c_str(), prompt.size(), NULL, 0, is_first, true);
    std::vector<llama_token> prompt_tokens(n_prompt_tokens);
    if (llama_tokenize(vocab, prompt.c_str(), prompt.size(), prompt_tokens.data(), prompt_tokens.size(), is_first, true) < 0) 
    {
        GGML_ABORT("failed to tokenize the prompt\n");
    }

    // prepare a batch for the prompt
    llama_batch batch = llama_batch_get_one(prompt_tokens.data(), prompt_tokens.size());
    llama_token new_token_id;
    while (true) 
    {
        // check if we have enough space in the context to evaluate this batch
        int n_ctx = llama_n_ctx(ctx);
        int n_ctx_used = llama_get_kv_cache_used_cells(ctx);
        if (n_ctx_used + batch.n_tokens > n_ctx) 
        {
            printf("\033[0m\n");
            fprintf(stderr, "context size exceeded\n");
            exit(0);
        }

        if (llama_decode(ctx, batch)) 
        {
            GGML_ABORT("failed to decode\n");
        }

        // sample the next token
        new_token_id = llama_sampler_sample(smpl, ctx, -1);

        // is it an end of generation?
        if (llama_vocab_is_eog(vocab, new_token_id)) 
        {
            break;
        }

        // convert the token to a string, print it and add it to the response
        char buf[256];
        int n = llama_token_to_piece(vocab, new_token_id, buf, sizeof(buf), 0, true);
        if (n < 0) 
        {
            GGML_ABORT("failed to convert token to piece\n");
        }
        std::string piece(buf, n);
        printf("%s", piece.c_str());
        fflush(stdout);
        response += piece;

        // prepare the next batch with the sampled token
        batch = llama_batch_get_one(&new_token_id, 1);
    }
    
    //ok this is my code agian.
    token_response* resp = req->response_;
    write_scope _(resp->lock_);
    resp->result_ = new char[response.size() + 1];
    resp->result_[response.size()] = '\0';
    resp->status_ = token_response::Success;
    memcpy(resp->result_, response.c_str(), response.size());
}

token_response* get_token_async(const char* prompt, loaded_model* model, const int n_ctx)
{
    gen_token_req* req = new gen_token_req();
    req->model_ = model;
    req->prompt_ = prompt;
    req->n_ctx = n_ctx;
    req->response_ = new token_response();

    schedule(req, generate_token);

    return req->response_;
}

//adapt this to ur use case if u need, im lazy and doing this for free after all.
#define MOD_PUBLIC_API __declspec(dllexport)

extern "C"
{
MOD_PUBLIC_API token_response* GenerateToken(const char* prompt, loaded_model* model, const int n_ctx)
{
    return get_token_async(prompt, model, n_ctx);
}
MOD_PUBLIC_API bool PollToken(token_response* response)
{
    read_scope _(response->lock_);
    return response->status_ != token_response::Generating;
}

MOD_PUBLIC_API const char* RetrieveToken(token_response* response)
{
    //TODO: handle failure.
    read_scope _(response->lock_);
    const char* token = response->result_;
    return token;
}

MOD_PUBLIC_API void FreeToken(token_response* response)
{
    //Bad things will happen if we try to free token while the job is in flight just uhh..dont do that.
    {
        write_scope _(response->lock_);
        if (response->result_ != nullptr)
        {
            free(response->result_);
        }
    }
    delete response; // Free the dynamically allocated memory
}

MOD_PUBLIC_API loaded_model* LoadModel(const char* model_path, const int ngl)
{
    return load_model_async(model_path, ngl);
}

MOD_PUBLIC_API void FreeModel(loaded_model* model)
{
    //todo: refcount the model.
    if (model->signature == 0xdeadead)
    {
        llama_model_free(model->inner_);
        delete model;
    }
}

}
